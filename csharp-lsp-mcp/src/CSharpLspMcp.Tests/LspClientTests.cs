using CSharpLspMcp.Lsp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CSharpLspMcp.Tests;

public class LspClientTests
{
    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<LspClient>();
        var filter = new SolutionFilter(LoggerFactory.Create(_ => { }).CreateLogger<SolutionFilter>());
        var client = new LspClient(logger, filter);

        await client.DisposeAsync();
        await client.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_AfterDisposeAsync_DoesNotThrow()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<LspClient>();
        var filter = new SolutionFilter(LoggerFactory.Create(_ => { }).CreateLogger<SolutionFilter>());
        var client = new LspClient(logger, filter);

        await client.DisposeAsync();
        await client.StopAsync();
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentWithStopAsync_DoesNotThrow()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<LspClient>();
        var filter = new SolutionFilter(LoggerFactory.Create(_ => { }).CreateLogger<SolutionFilter>());
        var client = new LspClient(logger, filter);

        var disposeTask = client.DisposeAsync().AsTask();
        var stopTask   = client.StopAsync();

        await Task.WhenAll(disposeTask, stopTask);
    }
}
