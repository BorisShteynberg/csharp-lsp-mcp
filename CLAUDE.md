# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Is

An MCP (Model Context Protocol) server that gives AI assistants access to C# language intelligence. It bridges Claude/LLMs to `csharp-ls` (a C# Language Server) via LSP, and includes a built-in XAML analyzer. Published as a .NET global tool (`dotnet tool install --global csharp-lsp-mcp`).

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
                                                    Roslyn compiler
```

**Key components:**

- **`Program.cs`** — Entry point. Configures .NET Host with DI, registers the MCP server using the official `ModelContextProtocol` SDK, and wires up `CSharpTools` and `XamlTools`. Logs to stderr (not stdout) to preserve the MCP protocol stream.

- **`Lsp/LspClient.cs`** — Manages the `csharp-ls` process lifecycle and speaks LSP (JSON-RPC over stdin/stdout). Thread-safe via semaphore. Caches diagnostics. Key concern: LSP holds file locks on DLLs, so `csharp_stop` must be called before rebuilding. Operations use a 3-minute timeout.

- **`Lsp/SolutionFilter.cs`** — Generates a filtered `.slnf` file to exclude project types `csharp-ls` can't handle (`.wixproj`, `.sqlproj`, `.vcxproj`, `.vbproj`, `.fsproj`). This prevents LSP timeouts on complex mixed solutions.

- **`Tools/CSharpTools.cs`** — Exposes 9 MCP tools (`csharp_set_workspace`, `csharp_stop`, `csharp_diagnostics`, `csharp_hover`, `csharp_completions`, `csharp_definition`, `csharp_references`, `csharp_symbols`, `csharp_code_actions`, `csharp_rename`). Auto-discovers `.sln`/`.csproj` from a file path if workspace isn't set explicitly.

- **`Tools/XamlTools.cs` + `Xaml/`** — 7 built-in XAML tools with no external dependency. Uses a tolerant XML parser and includes WPF control type definitions, binding extraction via regex, and common typo detection.

- **`Lsp/LspTypes.cs`** — LSP v3 protocol types. All JSON uses camelCase (`JsonSerializerOptions`).

## Testing

xUnit + Moq. Tests cover LSP stream-reading (content-length framing), protocol type deserialization, and XAML parsing/validation. The LSP tests use `MemoryStream` rather than a real process.

## Important Constraints

- **MCP protocol on stdout**: all logging must go to stderr. Don't add `Console.WriteLine` to tool code.
- **Nullable reference types** are enabled project-wide (`<Nullable>enable</Nullable>`).
- **LSP file locks**: after `csharp-ls` starts, it holds locks on compiled DLLs. Tools that need a clean build must call `csharp_stop` first.
- **MCP SDK version**: uses `ModelContextProtocol` `0.5.0-preview.1` — a pre-release SDK with an evolving API surface.
