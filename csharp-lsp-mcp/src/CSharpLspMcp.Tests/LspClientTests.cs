using System.Reflection;
using System.Text;
using CSharpLspMcp.Lsp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CSharpLspMcp.Tests;

public class LspClientTests
{
    [Fact]
    public async Task ReadContentLength_ReadsLengthAndPayloadAsync()
    {
        var payload = Encoding.UTF8.GetBytes("{\"a\":\"✓\"}");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        var stream = new MemoryStream(header.Concat(payload).ToArray());

        var readLength = await InvokeReadContentLengthAsync(stream);
        Assert.Equal(payload.Length, readLength);

        var readBuffer = new byte[payload.Length];
        var readOk = await InvokeReadExactAsync(stream, readBuffer, payload.Length);
        Assert.True(readOk);
        Assert.Equal(payload, readBuffer);
    }

    [Fact]
    public async Task ReadExact_ReturnsFalseOnShortStreamAsync()
    {
        var stream = new MemoryStream(new byte[] { 1, 2 });
        var readBuffer = new byte[3];

        var readOk = await InvokeReadExactAsync(stream, readBuffer, readBuffer.Length);

        Assert.False(readOk);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<LspClient>();
        var filter = new SolutionFilter(LoggerFactory.Create(_ => { }).CreateLogger<SolutionFilter>());
        var client = new LspClient(logger, filter);

        await client.DisposeAsync(); // first call
        await client.DisposeAsync(); // second call — must not throw
    }

    [Fact]
    public async Task StopAsync_AfterDisposeAsync_DoesNotThrow()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<LspClient>();
        var filter = new SolutionFilter(LoggerFactory.Create(_ => { }).CreateLogger<SolutionFilter>());
        var client = new LspClient(logger, filter);

        await client.DisposeAsync();
        await client.StopAsync(); // must not throw ObjectDisposedException
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentWithStopAsync_DoesNotThrow()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<LspClient>();
        var filter = new SolutionFilter(LoggerFactory.Create(_ => { }).CreateLogger<SolutionFilter>());
        var client = new LspClient(logger, filter);

        // Fire both concurrently; neither should throw.
        var disposeTask = client.DisposeAsync().AsTask();
        var stopTask   = client.StopAsync();

        await Task.WhenAll(disposeTask, stopTask);
    }

    private static Task<int?> InvokeReadContentLengthAsync(Stream stream)
    {
        var method = typeof(LspClient).GetMethod(
            "ReadContentLengthAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = (Task<int?>)method.Invoke(null, new object?[] { stream, CancellationToken.None })!;
        return task;
    }

    private static Task<bool> InvokeReadExactAsync(Stream stream, byte[] buffer, int length)
    {
        var method = typeof(LspClient).GetMethod(
            "ReadExactAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = (Task<bool>)method.Invoke(null, new object?[] { stream, buffer, length, CancellationToken.None })!;
        return task;
    }
}
