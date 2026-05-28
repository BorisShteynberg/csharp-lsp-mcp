using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CSharpLspMcp.Lsp;

public abstract class LspProcessClient : IAsyncDisposable
{
    protected readonly ILogger _logger;
    protected Process? _lspProcess;
    protected Stream? _outputStream;
    private int _requestId;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    protected CancellationTokenSource? _readLoopCts;
    protected Task? _readLoopTask;
    protected bool _isInitialized;
    protected readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    protected LspProcessClient(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsRunning => _isInitialized && _lspProcess != null && !_lspProcess.HasExited;

    public abstract Task StopAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await StopAsync();
    }

    protected async Task ShutdownProcessAsync()
    {
        if (_readLoopCts != null)
            await _readLoopCts.CancelAsync();

        if (_readLoopTask != null)
        {
            try { await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }

        if (_lspProcess != null && !_lspProcess.HasExited)
        {
            try
            {
                await SendRequestAsync<object>("shutdown", null, CancellationToken.None);
                await SendNotificationAsync("exit", null, CancellationToken.None);
                if (!_lspProcess.WaitForExit(3000))
                    _lspProcess.Kill();
            }
            catch
            {
                try { _lspProcess.Kill(); } catch { }
            }
        }

        _lspProcess?.Dispose();
        _readLoopCts?.Dispose();

        _lspProcess = null;
        _outputStream = null;
        _readLoopCts = null;
        _readLoopTask = null;
        _isInitialized = false;
        _requestId = 0;
        _pendingRequests.Clear();
    }

    protected async Task CleanupFailedStartAsync()
    {
        if (_readLoopCts != null)
            await _readLoopCts.CancelAsync();

        if (_readLoopTask != null)
        {
            try { await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }

        if (_lspProcess != null && !_lspProcess.HasExited)
        {
            try { _lspProcess.Kill(); } catch { }
        }

        _lspProcess?.Dispose();
        _readLoopCts?.Dispose();

        _outputStream = null;
        _readLoopCts = null;
        _readLoopTask = null;
        _lspProcess = null;
    }

    protected async Task<T?> SendRequestAsync<T>(string method, object? @params, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = @params
        };

        var tcs = new TaskCompletionSource<JsonElement>();
        _pendingRequests[id] = tcs;

        try
        {
            _logger.LogDebug("SendRequestAsync: Sending request id={Id} method={Method}", id, method);
            await SendMessageAsync(request, cancellationToken);
            _logger.LogDebug("SendRequestAsync: Request sent, waiting for response...");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            var result = await tcs.Task.WaitAsync(cts.Token);

            if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
                return default;

            return result.Deserialize<T>(JsonOptions);
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    protected async Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken)
    {
        var notification = new JsonRpcNotification
        {
            Method = method,
            Params = @params
        };
        await SendMessageAsync(notification, cancellationToken);
    }

    private async Task SendMessageAsync(object message, CancellationToken cancellationToken)
    {
        if (_lspProcess?.StandardInput?.BaseStream == null)
            throw new InvalidOperationException("LSP client not started");

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var content = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
        var bytes = Encoding.UTF8.GetBytes(content);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _lspProcess.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
            await _lspProcess.StandardInput.BaseStream.FlushAsync(cancellationToken);
            _logger.LogTrace("Sent: {Message}", json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    protected async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("ReadLoopAsync: Starting read loop");
        if (_outputStream == null)
        {
            _logger.LogError("ReadLoopAsync: Output stream is null!");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("ReadLoopAsync: Waiting for content length header...");
                var contentLength = await ReadContentLengthAsync(_outputStream, cancellationToken);
                if (contentLength == null)
                {
                    _logger.LogWarning("ReadLoopAsync: Stream closed (null content length)");
                    return;
                }

                _logger.LogDebug("ReadLoopAsync: Content-Length: {Length}", contentLength.Value);
                if (contentLength.Value <= 0)
                    continue;

                var payload = new byte[contentLength.Value];
                var readOk = await ReadExactAsync(_outputStream, payload, payload.Length, cancellationToken);
                if (!readOk)
                {
                    _logger.LogWarning("ReadLoopAsync: Stream closed while reading payload");
                    return;
                }

                var json = Encoding.UTF8.GetString(payload);
                _logger.LogTrace("Received: {Message}", json);
                _logger.LogDebug("ReadLoopAsync: Received message, length={Length}", json.Length);

                ProcessMessage(json);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("ReadLoopAsync: Cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LSP read loop");
            }
        }
        _logger.LogDebug("ReadLoopAsync: Exiting read loop");
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var hasId = root.TryGetProperty("id", out var idElement);
            var hasMethod = root.TryGetProperty("method", out var methodElement);

            if (hasId && hasMethod)
            {
                // Server-initiated request (e.g. client/registerCapability, workspace/configuration).
                // Send back a null result so the server is not blocked waiting for a response.
                var rawId = idElement.Clone();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var method = methodElement.GetString() ?? "unknown";
                        _logger.LogDebug("Server request received: {Method} — responding null", method);
                        var response = $"{{\"jsonrpc\":\"2.0\",\"id\":{rawId.GetRawText()},\"result\":null}}";
                        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(response)}\r\n\r\n{response}");
                        await _writeLock.WaitAsync();
                        try
                        {
                            if (_lspProcess?.StandardInput?.BaseStream != null)
                            {
                                await _lspProcess.StandardInput.BaseStream.WriteAsync(bytes);
                                await _lspProcess.StandardInput.BaseStream.FlushAsync();
                            }
                        }
                        finally { _writeLock.Release(); }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to respond to server request");
                    }
                });
                return;
            }

            if (hasId)
            {
                if (!idElement.TryGetInt32(out var id)) return;
                if (_pendingRequests.TryGetValue(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        var errorMsg = error.GetProperty("message").GetString();
                        _logger.LogError("LSP error: {Error}", errorMsg);
                        tcs.TrySetException(new Exception($"LSP error: {errorMsg}"));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        tcs.TrySetResult(result.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(default);
                    }
                }
            }
            else if (hasMethod)
            {
                var method = methodElement.GetString();
                if (method != null && root.TryGetProperty("params", out var @params))
                    HandleNotification(method, @params.Clone());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing LSP message");
        }
    }

    protected virtual void HandleNotification(string method, JsonElement @params) { }

    private static async Task<int?> ReadContentLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var lastFour = new byte[4];
        var lastIndex = 0;

        while (true)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
                return null;

            var value = buffer[0];
            headerBytes.Add(value);

            lastFour[lastIndex % 4] = value;
            lastIndex++;

            if (lastIndex >= 4 &&
                lastFour[(lastIndex - 4) % 4] == '\r' &&
                lastFour[(lastIndex - 3) % 4] == '\n' &&
                lastFour[(lastIndex - 2) % 4] == '\r' &&
                lastFour[(lastIndex - 1) % 4] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray()).TrimEnd('\r', '\n');

        var lines = headerText.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries);

        int length = 0;
        foreach (var _ in from line in lines
                          where line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                          where int.TryParse(line.Substring(15).Trim(), out length)
                          select new { })
        {
            return length;
        }

        return 0;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), cancellationToken);
            if (read == 0)
                return false;
            totalRead += read;
        }

        return true;
    }
}
