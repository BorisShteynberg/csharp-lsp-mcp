# csharp-lsp-mcp

[![Build](https://github.com/BorisShteynberg/csharp-lsp-mcp/actions/workflows/build.yml/badge.svg)](https://github.com/BorisShteynberg/csharp-lsp-mcp/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/CSharpLspMcp.svg)](https://www.nuget.org/packages/CSharpLspMcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CSharpLspMcp.svg)](https://www.nuget.org/packages/CSharpLspMcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![MCP](https://img.shields.io/badge/MCP-0.5.0-blue)](https://modelcontextprotocol.io/)
[![GitHub release](https://img.shields.io/github/v/release/BorisShteynberg/csharp-lsp-mcp)](https://github.com/BorisShteynberg/csharp-lsp-mcp/releases)

An MCP (Model Context Protocol) server that provides C#, Razor, and XAML language intelligence for AI assistants like Claude. It bridges the gap between LLMs and .NET development by exposing IntelliSense, diagnostics, and code analysis through the standardized MCP protocol.

## Features

### C# Language Intelligence (via csharp-ls)
- **Diagnostics** — Compiler errors and warnings in real-time
- **Hover Information** — Type information and documentation
- **IntelliSense Completions** — Context-aware code suggestions
- **Go to Definition** — Navigate to symbol definitions
- **Find References** — Locate all usages of a symbol
- **Document Symbols** — List all symbols in a file
- **Code Actions** — Quick fixes and refactorings
- **Rename Preview** — Preview symbol renames across the workspace
- **Stop/Restart** — Release file locks before rebuilding

### Razor Diagnostics (via Roslyn Language Server)
- **Diagnostics** — Compiler errors and warnings in `.cshtml` files
- **Go to Definition** — Navigate from Razor markup to C# definitions

### XAML Analysis (built-in, no external dependency)
- **Validation** — Check XAML for errors and issues
- **Binding Analysis** — Extract and analyze data bindings
- **Resource Inspection** — List and verify resource references
- **Name Discovery** — Find all x:Name declarations
- **Structure Visualization** — View element tree hierarchy
- **Binding Error Detection** — Identify binding problems
- **ViewModel Generation** — Generate ViewModels from bindings

## Prerequisites

1. **.NET 8.0 SDK** or later

2. **csharp-ls** — for C# tools:
   ```bash
   dotnet tool install --global csharp-ls
   ```

3. **VS Code C# extension** (`ms-dotnettools.csharp`) — for Razor tools. Install it in VS Code or download it manually. The extension ships `Microsoft.CodeAnalysis.LanguageServer.exe` and the Razor extension DLL that this server uses.

## Installation

### As a .NET global tool

```bash
dotnet tool install --global csharp-lsp-mcp
```

### From Source

```bash
git clone https://github.com/BorisShteynberg/csharp-lsp-mcp.git
cd csharp-lsp-mcp/csharp-lsp-mcp
dotnet build -c Release
```

## Adding to a Claude Code Project

### Automated (PowerShell — Windows)

Run the included script from the repo root:

```powershell
# Add to the current directory's project
.\add-to-project.ps1

# Add to a specific project
.\add-to-project.ps1 C:\path\to\your\project
```

The script edits `~/.claude.json` to register the server for that project, using the global tool if installed or the local debug build otherwise.

### Manual

Add the following to your project's entry in `~/.claude.json` (under `projects.<your-path>.mcpServers`):

```json
{
  "csharp-lsp-mcp": {
    "type": "stdio",
    "command": "csharp-lsp-mcp",
    "args": [],
    "env": {}
  }
}
```

### Claude Desktop

```json
{
  "mcpServers": {
    "csharp-lsp-mcp": {
      "command": "csharp-lsp-mcp",
      "args": []
    }
  }
}
```

## Usage

### Setting Up the Workspace

Before using C# or Razor tools, set the workspace directory:

```
Use csharp_set_workspace with path: "C:/path/to/your/solution"
```

This starts `csharp-ls` for C# tooling and kicks off the Roslyn Language Server in the background for Razor support, so both are ready by the time you need them.

### Stopping the LSP for Rebuilds

The LSP server holds file locks on project DLLs. Stop it before rebuilding your project:

```
Stop the C# LSP server so I can rebuild
```

After rebuilding, call `csharp_set_workspace` again to restart.

To stop all LSP servers and shut down the MCP server process itself (e.g. to rebuild `csharp-lsp-mcp` from source), use `mcp_stop`. Note that Claude Code will auto-restart the server, so disconnect via `/mcp` first if you need the DLL unlocked for a build.

### Example Interactions

```
Check for errors in Program.cs
What type is the variable at line 15, column 10 in MyClass.cs?
Find all usages of the GetCustomer method
Show me all data bindings in MainWindow.xaml
Get diagnostics for Pages/Index.cshtml
```

## Available Tools

| Tool | Description |
|------|-------------|
| `csharp_set_workspace` | Set the solution/project directory |
| `csharp_stop` | Stop the C# LSP server to release file locks |
| `mcp_stop` | Stop all LSP child processes and shut down the server |
| `csharp_diagnostics` | Get compiler errors and warnings |
| `csharp_hover` | Get type info at a position |
| `csharp_completions` | Get IntelliSense completions |
| `csharp_definition` | Go to definition |
| `csharp_references` | Find all references |
| `csharp_symbols` | Get document symbols |
| `csharp_code_actions` | Get available code actions |
| `csharp_rename` | Preview symbol rename |
| `razor_diagnostics` | Get compiler diagnostics for a `.cshtml` file |
| `razor_definition` | Go to definition from a Razor file position |
| `razor_stop` | Stop the Razor language server to release file locks |
| `xaml_validate` | Validate XAML for errors |
| `xaml_bindings` | Extract data bindings |
| `xaml_resources` | List resource references |
| `xaml_names` | List x:Name declarations |
| `xaml_structure` | Show element tree |
| `xaml_find_binding_errors` | Find binding errors |
| `xaml_extract_viewmodel` | Generate ViewModel from bindings |

## Command Line Options

```
csharp-lsp-mcp [OPTIONS]

OPTIONS:
    -h, --help      Show help message
    -v, --version   Show version information
    -V, --verbose   Enable verbose logging (to stderr)

ENVIRONMENT VARIABLES:
    MCP_DEBUG=1     Enable trace-level logging
```

## Architecture

```
┌─────────────────┐     MCP Protocol      ┌──────────────────┐
│  Claude / LLM   │◄────────────────────►│  csharp-lsp-mcp  │
└─────────────────┘                       └──┬───────────┬───┘
                                             │           │
                              ┌──────────────┘           └──────────────┐
                              │                                          │
                        ┌─────▼─────┐    ┌──────────────────┐   ┌──────▼──────┐
                        │ csharp-ls │    │  Roslyn Language  │   │    XAML     │
                        │   (LSP)   │    │  Server + Razor   │   │   Parser   │
                        └─────┬─────┘    └──────────────────┘   └─────────────┘
                              │
                        ┌─────▼─────┐
                        │  Roslyn   │
                        │ Compiler  │
                        └───────────┘
```

## Building from Source

```bash
git clone https://github.com/BorisShteynberg/csharp-lsp-mcp.git
cd csharp-lsp-mcp/csharp-lsp-mcp

# Build
dotnet build

# Run tests
dotnet test

# Pack as NuGet tool
dotnet pack src/CSharpLspMcp -c Release
```

## Contributing

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for a history of changes.

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [Model Context Protocol](https://modelcontextprotocol.io/) — The protocol specification
- [csharp-ls](https://github.com/razzmatazz/csharp-language-server) — The C# Language Server
- [Microsoft MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — The official C# SDK for MCP

## Related Projects

- [Claude Code](https://claude.ai/code) — Anthropic's CLI for Claude
- [MCP Servers](https://github.com/modelcontextprotocol/servers) — Official MCP server implementations
