using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Lsp;

public class LspClient : LspProcessClient
{
    private readonly SolutionFilter _solutionFilter;
    private readonly ConcurrentDictionary<string, PublishDiagnosticsParams> _diagnosticsCache = new();
    private string? _filteredWorkspacePath;

    public event Action<PublishDiagnosticsParams>? DiagnosticsReceived;

    public string? WorkspacePath => _filteredWorkspacePath;

    public LspClient(ILogger<LspClient> logger, SolutionFilter solutionFilter) : base(logger)
    {
        _solutionFilter = solutionFilter;
    }

    /// <summary>
    /// Stops the LSP server and resets state so it can be restarted with StartAsync.
    /// </summary>
    public override async Task StopAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (!_isInitialized && _lspProcess == null)
                return;

            _logger.LogInformation("Stopping LSP server...");
            await ShutdownProcessAsync();
            _filteredWorkspacePath = null;
            _diagnosticsCache.Clear();
            _solutionFilter.Cleanup();
            _logger.LogInformation("LSP server stopped.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> StartAsync(string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
                return true;

            // Try to find csharp-ls
            var lspPath = await FindLspServerAsync(cancellationToken);
            if (lspPath == null)
            {
                _logger.LogError("Could not find csharp-ls. Install it with: dotnet tool install --global csharp-ls");
                return false;
            }

            // Filter the solution to exclude unsupported project types
            var effectiveWorkspacePath = workspacePath;
            if (workspacePath != null)
            {
                _filteredWorkspacePath = _solutionFilter.GetFilteredWorkspacePath(workspacePath);
                if (_filteredWorkspacePath != workspacePath)
                {
                    _logger.LogInformation("Using filtered workspace: {Path}", _filteredWorkspacePath);
                    effectiveWorkspacePath = _filteredWorkspacePath;
                }
            }

            _logger.LogInformation("Starting LSP server: {Path}", lspPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = lspPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Don't set encoding - we write bytes directly to avoid BOM issues
            };

            _lspProcess = new Process { StartInfo = startInfo };
            _lspProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("LSP stderr: {Message}", e.Data);
            };

            if (!_lspProcess.Start())
            {
                _logger.LogError("Failed to start LSP process");
                return false;
            }

            _logger.LogDebug("LSP process started, PID: {Pid}", _lspProcess.Id);
            _lspProcess.BeginErrorReadLine();

            // Small delay to let the process start
            await Task.Delay(100, cancellationToken);

            if (_lspProcess.HasExited)
            {
                _logger.LogError("LSP process exited immediately with code: {ExitCode}", _lspProcess.ExitCode);
                return false;
            }
            _outputStream = _lspProcess.StandardOutput.BaseStream;

            _readLoopCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);

            // Initialize the LSP server with the (potentially filtered) workspace
            var initResult = await InitializeAsync(effectiveWorkspacePath, cancellationToken);
            if (initResult == null)
            {
                _logger.LogError("LSP initialization failed");
                await CleanupFailedStartAsync();
                return false;
            }

            _logger.LogInformation("LSP server initialized: {ServerName} {Version}",
                initResult.ServerInfo?.Name ?? "Unknown",
                initResult.ServerInfo?.Version ?? "Unknown");

            // Send initialized notification
            await SendNotificationAsync("initialized", new { }, cancellationToken);

            _isInitialized = true;
            return true;
        }
        catch
        {
            await CleanupFailedStartAsync();
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string?> FindLspServerAsync(CancellationToken cancellationToken)
    {
        // Check common locations for csharp-ls
        var isWindows = OperatingSystem.IsWindows();
        var exeExtension = isWindows ? ".exe" : "";

        var possiblePaths = new[]
        {
            "csharp-ls", // In PATH (Windows handles .exe automatically)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", $"csharp-ls{exeExtension}"),
            "/usr/local/bin/csharp-ls",
            "/usr/bin/csharp-ls"
        };

        foreach (var path in possiblePaths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    if (process.ExitCode == 0)
                        return path;
                }
            }
            catch
            {
                // Try next path
            }
        }

        return null;
    }

    private async Task<InitializeResult?> InitializeAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("InitializeAsync: Sending initialize request to LSP server...");
        var rootUri = workspacePath != null ? new Uri(workspacePath).ToString() : null;

        var initParams = new InitializeParams
        {
            ProcessId = Environment.ProcessId,
            RootUri = rootUri,
            RootPath = workspacePath,
            Capabilities = new ClientCapabilities
            {
                TextDocument = new TextDocumentClientCapabilities
                {
                    Synchronization = new TextDocumentSyncClientCapabilities
                    {
                        DynamicRegistration = false,
                        WillSave = false,
                        DidSave = true
                    },
                    Completion = new CompletionClientCapabilities
                    {
                        DynamicRegistration = false,
                        CompletionItem = new CompletionItemCapabilities
                        {
                            SnippetSupport = true,
                            DocumentationFormat = ["markdown", "plaintext"]
                        }
                    },
                    Hover = new HoverClientCapabilities
                    {
                        DynamicRegistration = false,
                        ContentFormat = ["markdown", "plaintext"]
                    },
                    PublishDiagnostics = new PublishDiagnosticsClientCapabilities
                    {
                        RelatedInformation = true
                    }
                },
                Workspace = new WorkspaceClientCapabilities
                {
                    WorkspaceFolders = true
                }
            },
            WorkspaceFolders = workspacePath != null
                ? [ new WorkspaceFolder { Uri = rootUri!, Name = Path.GetFileName(workspacePath) } ]
                : null
        };

        _logger.LogDebug("InitializeAsync: Workspace folders: {Folders}", workspacePath);
        var response = await SendRequestAsync<InitializeResult>("initialize", initParams, cancellationToken);
        _logger.LogInformation("InitializeAsync: Received response from LSP server");
        return response;
    }

    public async Task OpenDocumentAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var param = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "csharp",
                Version = 1,
                Text = content
            }
        };

        await SendNotificationAsync("textDocument/didOpen", param, cancellationToken);
        _logger.LogDebug("Opened document: {Uri}", uri);
    }

    public async Task UpdateDocumentAsync(string filePath, string content, int version, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var param = new DidChangeTextDocumentParams
        {
            TextDocument = new VersionedTextDocumentIdentifier
            {
                Uri = uri,
                Version = version
            },
            ContentChanges = [ new TextDocumentContentChangeEvent { Text = content } ]
        };

        await SendNotificationAsync("textDocument/didChange", param, cancellationToken);
        _logger.LogDebug("Updated document: {Uri} (version {Version})", uri, version);
    }

    public async Task CloseDocumentAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var param = new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri }
        };

        await SendNotificationAsync("textDocument/didClose", param, cancellationToken);
        _diagnosticsCache.TryRemove(uri, out _);
        _logger.LogDebug("Closed document: {Uri}", uri);
    }

    public PublishDiagnosticsParams? GetCachedDiagnostics(string filePath)
    {
        var uri = new Uri(filePath).ToString();
        _diagnosticsCache.TryGetValue(uri, out var diagnostics);
        return diagnostics;
    }

    public async Task<PublishDiagnosticsParams?> WaitForDiagnosticsAsync(string filePath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(filePath).ToString();
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_diagnosticsCache.TryGetValue(uri, out var diagnostics))
                return diagnostics;

            await Task.Delay(100, cancellationToken);
        }

        return GetCachedDiagnostics(filePath);
    }

    public async Task<Hover?> GetHoverAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var param = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        return await SendRequestAsync<Hover>("textDocument/hover", param, cancellationToken);
    }

    public async Task<CompletionItem[]?> GetCompletionsAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var param = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character },
            Context = new CompletionContext { TriggerKind = CompletionTriggerKind.Invoked }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/completion", param, cancellationToken);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
            return null;

        // Response can be CompletionItem[] or CompletionList
        if (result.ValueKind == JsonValueKind.Array)
            return result.Deserialize<CompletionItem[]>(JsonOptions);

        var list = result.Deserialize<CompletionList>(JsonOptions);
        return list?.Items;
    }

    public async Task<Location[]?> GetDefinitionAsync(string filePath, int line, int character, CancellationToken cancellationToken = default)
    {
        var param = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/definition", param, cancellationToken);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
            return null;

        // Response can be Location, Location[], or null
        if (result.ValueKind == JsonValueKind.Array)
            return result.Deserialize<Location[]>(JsonOptions);

        var single = result.Deserialize<Location>(JsonOptions);
        return single != null ? [single] : null;
    }

    public async Task<Location[]?> GetReferencesAsync(string filePath, int line, int character, bool includeDeclaration = true, CancellationToken cancellationToken = default)
    {
        var param = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character },
            Context = new ReferenceContext { IncludeDeclaration = includeDeclaration }
        };

        return await SendRequestAsync<Location[]>("textDocument/references", param, cancellationToken);
    }

    public async Task<object?> GetDocumentSymbolsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var param = new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/documentSymbol", param, cancellationToken);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
            return null;

        // Try to determine if it's DocumentSymbol[] or SymbolInformation[]
        if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
        {
            var first = result[0];
            if (first.TryGetProperty("selectionRange", out _))
                return result.Deserialize<DocumentSymbol[]>(JsonOptions);
            else
                return result.Deserialize<SymbolInformation[]>(JsonOptions);
        }

        return Array.Empty<DocumentSymbol>();
    }

    public async Task<CodeAction[]?> GetCodeActionsAsync(string filePath, Range range, Diagnostic[] diagnostics, CancellationToken cancellationToken = default)
    {
        var param = new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Range = range,
            Context = new CodeActionContext { Diagnostics = diagnostics }
        };

        return await SendRequestAsync<CodeAction[]>("textDocument/codeAction", param, cancellationToken);
    }

    public async Task<WorkspaceEdit?> RenameSymbolAsync(string filePath, int line, int character, string newName, CancellationToken cancellationToken = default)
    {
        var param = new RenameParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character },
            NewName = newName
        };

        return await SendRequestAsync<WorkspaceEdit>("textDocument/rename", param, cancellationToken);
    }

    protected override void HandleNotification(string method, JsonElement @params)
    {
        if (method != "textDocument/publishDiagnostics") return;
        var diagnostics = @params.Deserialize<PublishDiagnosticsParams>(JsonOptions);
        if (diagnostics == null) return;
        _diagnosticsCache[diagnostics.Uri] = diagnostics;
        DiagnosticsReceived?.Invoke(diagnostics);
        _logger.LogDebug("Received {Count} diagnostics for {Uri}",
            diagnostics.Diagnostics.Length, diagnostics.Uri);
    }
}
