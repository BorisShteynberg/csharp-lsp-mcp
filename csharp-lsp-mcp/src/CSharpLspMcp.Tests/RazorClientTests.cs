using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using CSharpLspMcp.Lsp;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CSharpLspMcp.Tests;

public class RazorClientTests
{
    // Test subclass that bypasses real Roslyn server discovery.
    private class RazorClientWithoutDiscovery(ILogger<RazorClient> logger) : RazorClient(logger)
    {
        protected override Task<RoslynServerInfo?> FindRoslynServerAsync(CancellationToken cancellationToken)
            => Task.FromResult<RoslynServerInfo?>(null);
    }

    [Fact]
    public async Task StartAsync_WhenRoslynServerNotFound_ReturnsFalse()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<RazorClient>();
        var client = new RazorClientWithoutDiscovery(logger);

        var result = await client.StartAsync("/some/project", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task HandleNotification_PublishDiagnostics_CompletesWaiter()
    {
        var logger = LoggerFactory.Create(_ => { }).CreateLogger<RazorClient>();
        var client = new RazorClientWithoutDiscovery(logger);

        const string uri = "file:///tmp/Index.cshtml";
        var tcs = new TaskCompletionSource<PublishDiagnosticsParams?>();

        // Inject waiter via reflection
        var waitersField = typeof(RazorClient)
            .GetField("_diagnosticsWaiters", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var waiters = (ConcurrentDictionary<string, TaskCompletionSource<PublishDiagnosticsParams?>>)
            waitersField.GetValue(client)!;
        waiters[uri] = tcs;

        var paramsJson = $$"""
            {
              "uri": "{{uri}}",
              "diagnostics": [
                {
                  "message": "Undefined tag helper",
                  "severity": 1,
                  "range": {
                    "start": { "line": 4, "character": 1 },
                    "end":   { "line": 4, "character": 7 }
                  }
                }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(paramsJson);

        typeof(RazorClient)
            .GetMethod("HandleNotification", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(client, ["textDocument/publishDiagnostics", doc.RootElement.Clone()]);

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(result);
        Assert.Equal(uri, result.Uri);
        Assert.Single(result.Diagnostics);
        Assert.Equal("Undefined tag helper", result.Diagnostics[0].Message);
        Assert.Equal(DiagnosticSeverity.Error, result.Diagnostics[0].Severity);
    }
}
