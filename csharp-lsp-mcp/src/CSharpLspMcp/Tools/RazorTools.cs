using System.ComponentModel;
using System.Text.Json;
using CSharpLspMcp.Lsp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpLspMcp.Tools;

[McpServerToolType]
public class RazorTools(RazorClient razorClient, ILogger<RazorTools> logger)
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(3);

    [McpServerTool(Name = "razor_diagnostics")]
    [Description("Get compiler diagnostics for a Razor (.cshtml) file.")]
    public Task<string> GetDiagnosticsAsync(
        [Description("Absolute path to the .cshtml file")] string filePath,
        [Description("File content; reads from disk if omitted")] string? content = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteToolAsync("razor_diagnostics", async ct =>
        {
            var resolvedContent = content;
            if (resolvedContent == null)
            {
                if (!File.Exists(filePath))
                    return $"Error: File not found: {filePath}";
                resolvedContent = await File.ReadAllTextAsync(filePath, ct);
            }

            if (!await EnsureStartedAsync(filePath, ct))
                return "Error: rzls not found — install .NET SDK 8.0.3+ or the VS Code C# extension.";

            var result = await razorClient.GetDiagnosticsAsync(filePath, resolvedContent, ct);
            if (result == null || result.Diagnostics.Length == 0)
                return "[]";

            return JsonSerializer.Serialize(result.Diagnostics.Select(d => new
            {
                message = d.Message,
                severity = d.Severity?.ToString() ?? "Unknown",
                line = d.Range.Start.Line,
                character = d.Range.Start.Character,
                endLine = d.Range.End.Line,
                endCharacter = d.Range.End.Character
            }));
        }, cancellationToken);
    }

    [McpServerTool(Name = "razor_definition")]
    [Description("Go to definition from a position in a Razor (.cshtml) file.")]
    public Task<string> GetDefinitionAsync(
        [Description("Absolute path to the .cshtml file")] string filePath,
        [Description("0-based line number")] int line,
        [Description("0-based character offset")] int character,
        CancellationToken cancellationToken = default)
    {
        return ExecuteToolAsync("razor_definition", async ct =>
        {
            if (!File.Exists(filePath))
                return $"Error: File not found: {filePath}";

            if (!await EnsureStartedAsync(filePath, ct))
                return "Error: rzls not found — install .NET SDK 8.0.3+ or the VS Code C# extension.";

            var locations = await razorClient.GetDefinitionAsync(filePath, line, character, ct);
            if (locations == null || locations.Length == 0)
                return "No definition found.";

            var loc = locations[0];
            return JsonSerializer.Serialize(new
            {
                filePath = new Uri(loc.Uri).LocalPath,
                line = loc.Range.Start.Line,
                character = loc.Range.Start.Character
            });
        }, cancellationToken);
    }

    private async Task<bool> EnsureStartedAsync(string filePath, CancellationToken ct)
    {
        if (razorClient.IsRunning) return true;
        var projectRoot = FindProjectRoot(filePath);
        if (projectRoot == null)
        {
            logger.LogWarning("No .csproj found for {Path}", filePath);
            return false;
        }
        return await razorClient.StartAsync(projectRoot, ct);
    }

    private static string? FindProjectRoot(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir != null)
        {
            if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private async Task<string> ExecuteToolAsync(
        string toolName, Func<CancellationToken, Task<string>> action, CancellationToken mcpToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(mcpToken);
        cts.CancelAfter(ToolTimeout);
        try
        {
            return await action(cts.Token);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Tool {ToolName} was cancelled", toolName);
            return mcpToken.IsCancellationRequested
                ? "Error: Operation was cancelled by the client."
                : $"Error: Operation timed out after {ToolTimeout.TotalSeconds} seconds.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing tool {ToolName}", toolName);
            return $"Error: {ex.Message}";
        }
    }
}
