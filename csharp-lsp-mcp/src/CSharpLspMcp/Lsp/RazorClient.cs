using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Lsp;

public class RazorClient : LspProcessClient
{
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
            if (_isInitialized)
                return true;

            var rzlsPath = await FindRazorServerAsync(cancellationToken);
            if (rzlsPath == null)
            {
                _logger.LogError("rzls not found — install .NET SDK 8.0.3+ or the VS Code C# extension.");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = rzlsPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _lspProcess = new Process { StartInfo = startInfo };
            _lspProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    _logger.LogWarning("rzls stderr: {Message}", e.Data);
            };

            if (!_lspProcess.Start())
            {
                _logger.LogError("Failed to start rzls process");
                return false;
            }

            _lspProcess.BeginErrorReadLine();
            await Task.Delay(100, cancellationToken);

            if (_lspProcess.HasExited)
            {
                _logger.LogError("rzls exited immediately with code: {ExitCode}", _lspProcess.ExitCode);
                return false;
            }

            _outputStream = _lspProcess.StandardOutput.BaseStream;
            _readLoopCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);

            var rootUri = new Uri(projectRoot).ToString();
            var initResult = await SendRequestAsync<JsonElement>("initialize", new
            {
                processId = Environment.ProcessId,
                rootUri,
                capabilities = new { textDocument = new { publishDiagnostics = new { } } }
            }, cancellationToken);

            if (initResult.ValueKind == JsonValueKind.Undefined)
            {
                _logger.LogError("Razor LSP initialize failed");
                await CleanupFailedStartAsync();
                return false;
            }

            await SendNotificationAsync("initialized", new { }, cancellationToken);

            try
            {
                await SendRequestAsync<JsonElement>("razor/initialize",
                    new { hostDocumentSyncVersion = 2 }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Razor LSP failed to initialize — rzls may require a running Roslyn host.");
                await CleanupFailedStartAsync();
                return false;
            }

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
                    LanguageId = "razor",
                    Version = 1,
                    Text = content
                }
            }, cancellationToken);
            _openDocuments[filePath] = 1;
        }
        else
        {
            var newVersion = version + 1;
            await SendNotificationAsync("textDocument/didChange", new DidChangeTextDocumentParams
            {
                TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = newVersion },
                ContentChanges = [new TextDocumentContentChangeEvent { Text = content }]
            }, cancellationToken);
            _openDocuments[filePath] = newVersion;
        }
    }

    protected virtual async Task<string?> FindRazorServerAsync(CancellationToken cancellationToken)
    {
        if (await TryVersionAsync("rzls", cancellationToken))
            return "rzls";

        var exeExtension = OperatingSystem.IsWindows() ? ".exe" : "";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-sdks",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            foreach (var (version, root) in ParseSdkList(output)
                .OrderByDescending(s => Version.TryParse(s.Version, out var v) ? v : new Version(0, 0)))
            {
                var major = version.Split('.')[0];
                var rzlsPath = Path.Combine(root, version, "DotnetTools", "dotnet-razor",
                    "tools", $"net{major}", "any", $"rzls{exeExtension}");
                if (await TryVersionAsync(rzlsPath, cancellationToken))
                    return rzlsPath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SDK-relative rzls discovery failed");
        }

        return null;
    }

    private static IEnumerable<(string Version, string Root)> ParseSdkList(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var bracketIdx = trimmed.IndexOf('[');
            if (bracketIdx < 0) continue;
            var version = trimmed[..bracketIdx].Trim();
            var root = trimmed[(bracketIdx + 1)..].TrimEnd(']', ' ', '\r');
            if (!string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(root))
                yield return (version, root);
        }
    }

    private static async Task<bool> TryVersionAsync(string path, CancellationToken cancellationToken)
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
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync(cancellationToken);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
