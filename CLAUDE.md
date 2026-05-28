# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Is

An MCP (Model Context Protocol) server that gives AI assistants access to C#, Razor, and XAML language intelligence. It bridges Claude/LLMs to:
- `csharp-ls` (a C# Language Server) via LSP for C# tools
- `Microsoft.CodeAnalysis.LanguageServer` (Roslyn LS, ships with the VS Code C# extension) via LSP for Razor tools
- A built-in XAML analyzer with no external dependency

Published as a .NET global tool (`dotnet tool install --global csharp-lsp-mcp`).

## Build & Test Commands

All commands run from `csharp-lsp-mcp/` (the subdirectory containing the `.sln`):

```bash
dotnet build                                    # Build all projects
dotnet test                                     # Run all tests
dotnet test --filter "ClassName"               # Run a single test class
dotnet run --project src/CSharpLspMcp         # Run locally (MCP server on stdio)
dotnet pack src/CSharpLspMcp -c Release       # Build NuGet package
```

The solution file is at `csharp-lsp-mcp/CSharpLspMcp.sln`. The main project targets `net8.0`.

## Architecture

```
Claude (MCP client) ←→ csharp-lsp-mcp (stdio) ←→ csharp-ls process (LSP/JSON-RPC)
                                                ↓
                              Microsoft.CodeAnalysis.LanguageServer (Roslyn LS + Razor)
```

**Key components:**

- **`Program.cs`** — Entry point. Configures .NET Host with DI, registers the MCP server using the official `ModelContextProtocol` SDK, and wires up `CSharpTools`, `RazorTools`, and `XamlTools`. Logs to stderr (not stdout) to preserve the MCP protocol stream.

- **`Lsp/LspProcessClient.cs`** — Abstract base class for LSP process clients. Manages process lifecycle (start, read loop, shutdown), JSON-RPC framing (Content-Length), request/response correlation, and server-initiated request handling. Per-request timeout is 5 minutes. Responds with `null` to any server-initiated request (id+method) to avoid deadlocking the server during initialization.

- **`Lsp/LspClient.cs`** — Manages the `csharp-ls` process. Thread-safe via semaphore. Caches diagnostics. Exposes `WorkspacePath` (the post-filter path used for initialization). Key concern: holds file locks on DLLs — call `csharp_stop` before rebuilding.

- **`Lsp/RazorClient.cs`** — Manages the `Microsoft.CodeAnalysis.LanguageServer` process for Razor support. Discovers the executable from the VS Code C# extension (`~/.vscode/extensions/ms-dotnettools.csharp-*/.roslyn/`). Launched with `--stdio --extension <razorExtPath> --clientProcessId <pid>`. Writes a startup/stderr log to `%TEMP%\roslyn-ls-stderr.log` for diagnostics.

- **`Lsp/SolutionFilter.cs`** — Generates a filtered `.sln` in a temp directory, excluding project types `csharp-ls` can't handle (`.wixproj`, `.sqlproj`, `.vcxproj`, `.vbproj`, `.fsproj`). Prevents LSP timeouts on mixed solutions.

- **`Tools/CSharpTools.cs`** — Exposes 10 MCP tools (`csharp_set_workspace`, `csharp_stop`, `csharp_diagnostics`, `csharp_hover`, `csharp_completions`, `csharp_definition`, `csharp_references`, `csharp_symbols`, `csharp_code_actions`, `csharp_rename`). Also fires `RazorClient.StartAsync` in the background when the workspace is set, so Roslyn LS is already loading by the time Razor tools are invoked.

- **`Tools/RazorTools.cs`** — Exposes 3 MCP tools (`razor_diagnostics`, `razor_definition`, `razor_stop`). Uses `LspClient.WorkspacePath` for the Roslyn LS workspace so it reuses the same filtered solution path.

- **`Tools/XamlTools.cs` + `Xaml/`** — 7 built-in XAML tools with no external dependency. Uses a tolerant XML parser, WPF control type definitions, binding extraction via regex, and common typo detection.

- **`Lsp/LspTypes.cs`** — LSP v3 protocol types. All JSON uses camelCase (`JsonSerializerOptions`).

## Testing

xUnit + Moq. Tests cover LSP stream-reading (content-length framing), protocol type deserialization, and XAML parsing/validation. The LSP tests use `MemoryStream` rather than a real process.

## Important Constraints

- **MCP protocol on stdout**: all logging must go to stderr. Don't add `Console.WriteLine` to tool code.
- **Nullable reference types** are enabled project-wide (`<Nullable>enable</Nullable>`).
- **LSP file locks**: after `csharp-ls` or Roslyn LS starts, they hold locks on compiled DLLs. Call `csharp_stop` or `razor_stop` before rebuilding. The `mcp_stop` tool stops all LSP child processes and shuts down the host, but Claude Code auto-restarts the server immediately — so to rebuild `csharp-lsp-mcp` itself, disconnect via `/mcp` first, build, then reconnect.
- **MCP SDK version**: uses `ModelContextProtocol` `0.5.0-preview.1` — a pre-release SDK with an evolving API surface.
- **Roslyn LS requires `--stdio`**: `Microsoft.CodeAnalysis.LanguageServer` version 2.x requires the `--stdio` flag to use stdin/stdout transport. Omitting it causes the process to exit immediately.
- **Eager Razor startup**: `csharp_set_workspace` fires `RazorClient.StartAsync` as a fire-and-forget background task. Roslyn LS can take 30–60 seconds to initialize a solution; starting it early avoids timeouts on the first `razor_diagnostics` call.
