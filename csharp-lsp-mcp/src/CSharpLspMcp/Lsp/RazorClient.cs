using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Lsp;

public class RazorClient : LspProcessClient
{
    protected record RoslynServerInfo(string LsPath, string RazorExtPath);

    private readonly ConcurrentDictionary<string, int> _openDocuments = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PublishDiagnosticsParams?>> _diagnosticsWaiters = new();

    public RazorClient(ILogger<RazorClient> logger) : base(logger) { }

    public override async Task StopAsync()
    {
        await _initLock.WaitAsync();
        try
        {
            if (!_isInitialized && _lspProcess == null)
                return;

            _logger.LogInformation("Stopping Razor LSP server...");

            foreach (var filePath in _openDocuments.Keys.ToList())
            {
                try
                {
                    await SendNotificationAsync("textDocument/didClose",
                        new { textDocument = new { uri = new Uri(filePath).ToString() } },
                        CancellationToken.None);
                }
                catch { }
            }
            _openDocuments.Clear();

            await ShutdownProcessAsync();
            _logger.LogInformation("Razor LSP server stopped.");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> StartAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized && _lspProcess != null && _lspProcess.HasExited)
            {
                _logger.LogWarning("Roslyn LS process exited unexpectedly, restarting...");
                await ShutdownProcessAsync();
                _openDocuments.Clear();
            }

            if (_isInitialized)
                return true;

            var serverInfo = await FindRoslynServerAsync(cancellationToken);
            if (serverInfo == null)
            {
                _logger.LogError("Could not find Microsoft.CodeAnalysis.LanguageServer. Install the VS Code C# extension (ms-dotnettools.csharp).");
                return false;
            }

            _logger.LogInformation("Starting Roslyn language server: {Path}", serverInfo.LsPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = serverInfo.LsPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--stdio");
            startInfo.ArgumentList.Add("--extension");
            startInfo.ArgumentList.Add(serverInfo.RazorExtPath);
            startInfo.ArgumentList.Add("--logLevel");
            startInfo.ArgumentList.Add("Warning");
            startInfo.ArgumentList.Add("--clientProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            var stderrLogPath = Path.Combine(Path.GetTempPath(), "roslyn-ls-stderr.log");
            File.WriteAllText(stderrLogPath, $"[{Environment.ProcessId}] Starting Roslyn LS: {serverInfo.LsPath}\n");

            _lspProcess = new Process { StartInfo = startInfo };
            _lspProcess.EnableRaisingEvents = true;
            _lspProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogDebug("Roslyn LS stderr: {Message}", e.Data);
                    File.AppendAllText(stderrLogPath, $"[stderr] {e.Data}\n");
                }
            };
            _lspProcess.Exited += (_, _) =>
            {
                _logger.LogWarning("Roslyn LS process exited with code {ExitCode}", _lspProcess?.ExitCode);
                File.AppendAllText(stderrLogPath, $"[exit] code={_lspProcess?.ExitCode}\n");
            };

            if (!_lspProcess.Start())
            {
                _logger.LogError("Failed to start Roslyn LS process");
                File.AppendAllText(stderrLogPath, "[error] Start() returned false\n");
                return false;
            }

            File.AppendAllText(stderrLogPath, $"[info] Process started, PID={_lspProcess.Id}\n");
            _lspProcess.BeginErrorReadLine();

            await Task.Delay(200, cancellationToken);

            if (_lspProcess.HasExited)
            {
                _logger.LogError("Roslyn LS process exited immediately with code: {ExitCode}", _lspProcess.ExitCode);
                File.AppendAllText(stderrLogPath, $"[error] Exited immediately, code={_lspProcess.ExitCode}\n");
                return false;
            }

            File.AppendAllText(stderrLogPath, "[info] Process alive after 200ms, sending initialize...\n");

            _outputStream = _lspProcess.StandardOutput.BaseStream;
            _readLoopCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);

            var rootUri = new Uri(projectRoot).ToString();
            var initParams = new
            {
                processId = Environment.ProcessId,
                rootUri = rootUri,
                rootPath = projectRoot,
                capabilities = new
                {
                    textDocument = new
                    {
                        publishDiagnostics = new { },
                        definition = new { dynamicRegistration = false },
                        synchronization = new { didSave = true }
                    },
                    workspace = new { workspaceFolders = true }
                },
                workspaceFolders = new[]
                {
                    new { uri = rootUri, name = Path.GetFileName(projectRoot) }
                }
            };

            var initResult = await SendRequestAsync<JsonElement>("initialize", initParams, cancellationToken);
            if (initResult.ValueKind == JsonValueKind.Undefined)
            {
                _logger.LogError("Roslyn LS initialization failed");
                File.AppendAllText(stderrLogPath, "[error] initialize returned undefined\n");
                await CleanupFailedStartAsync();
                return false;
            }

            File.AppendAllText(stderrLogPath, "[info] initialize succeeded, sending initialized notification\n");
            await SendNotificationAsync("initialized", new { }, cancellationToken);

            _isInitialized = true;
            File.AppendAllText(stderrLogPath, "[info] RazorClient fully initialized\n");
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

    protected override void HandleNotification(string method, JsonElement @params)
    {
        if (method != "textDocument/publishDiagnostics") return;
        var data = @params.Deserialize<PublishDiagnosticsParams>(JsonOptions);
        if (data == null) return;
        if (_diagnosticsWaiters.TryRemove(data.Uri, out var tcs))
            tcs.TrySetResult(data);
    }

    public async Task<PublishDiagnosticsParams?> GetDiagnosticsAsync(
        string filePath, string content, CancellationToken cancellationToken)
    {
        var uri = new Uri(filePath).ToString();
        var tcs = new TaskCompletionSource<PublishDiagnosticsParams?>();
        if (_diagnosticsWaiters.TryRemove(uri, out var existingTcs))
            existingTcs.TrySetResult(null);
        _diagnosticsWaiters[uri] = tcs;

        await EnsureDocumentSyncedAsync(filePath, content, cancellationToken);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _diagnosticsWaiters.TryRemove(uri, out _);
            _logger.LogWarning("Diagnostics timeout for {Path}", filePath);
            return null;
        }
    }

    public async Task<Location[]?> GetDefinitionAsync(
        string filePath, int line, int character, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        await EnsureDocumentSyncedAsync(filePath, content, cancellationToken);

        var param = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(filePath).ToString() },
            Position = new Position { Line = line, Character = character }
        };

        var result = await SendRequestAsync<JsonElement>("textDocument/definition", param, cancellationToken);
        if (result.ValueKind == JsonValueKind.Undefined || result.ValueKind == JsonValueKind.Null)
            return null;
        if (result.ValueKind == JsonValueKind.Array)
            return result.Deserialize<Location[]>(JsonOptions);
        var single = result.Deserialize<Location>(JsonOptions);
        return single != null ? [single] : null;
    }

    private async Task EnsureDocumentSyncedAsync(string filePath, string content, CancellationToken cancellationToken)
    {
        var uri = new Uri(filePath).ToString();

        if (!_openDocuments.TryGetValue(filePath, out var version))
        {
            await SendNotificationAsync("textDocument/didOpen", new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = uri,
                    LanguageId = "aspnetcorerazor",
                    Version = 1,
                    Text = content
                }
            }, cancellationToken);
            _openDocuments[filePath] = 1;
        }
        else
        {
            var newVersion = _openDocuments.AddOrUpdate(filePath, 1, (_, v) => v + 1);
            await SendNotificationAsync("textDocument/didChange", new DidChangeTextDocumentParams
            {
                TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = newVersion },
                ContentChanges = [new TextDocumentContentChangeEvent { Text = content }]
            }, cancellationToken);
        }
    }

    protected virtual Task<RoslynServerInfo?> FindRoslynServerAsync(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extRoot = Path.Combine(home, ".vscode", "extensions");
        if (!Directory.Exists(extRoot))
            return Task.FromResult<RoslynServerInfo?>(null);

        var exeName = OperatingSystem.IsWindows()
            ? "Microsoft.CodeAnalysis.LanguageServer.exe"
            : "Microsoft.CodeAnalysis.LanguageServer";

        var candidates = Directory.GetDirectories(extRoot, "ms-dotnettools.csharp-*")
            .OrderByDescending(d =>
            {
                // Extract semantic version from "ms-dotnettools.csharp-2.140.8-win32-x64"
                var name = Path.GetFileName(d);
                var start = "ms-dotnettools.csharp-".Length;
                if (start >= name.Length) return new Version(0, 0);
                var rest = name.Substring(start);
                var versionPart = rest.Split('-')[0];
                return Version.TryParse(versionPart, out var v) ? v : new Version(0, 0);
            });

        foreach (var extDir in candidates)
        {
            var lsPath = Path.Combine(extDir, ".roslyn", exeName);
            var razorExtPath = Path.Combine(extDir, ".razorExtension",
                "Microsoft.VisualStudioCode.RazorExtension.dll");

            if (File.Exists(lsPath) && File.Exists(razorExtPath))
                return Task.FromResult<RoslynServerInfo?>(new RoslynServerInfo(lsPath, razorExtPath));
        }

        return Task.FromResult<RoslynServerInfo?>(null);
    }
}
