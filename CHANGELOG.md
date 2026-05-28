# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-05-28

### Added
- Razor Pages support via the Roslyn Language Server (`Microsoft.CodeAnalysis.LanguageServer`)
  - `razor_diagnostics` — compiler errors and warnings for `.cshtml` files
  - `razor_definition` — go to definition from a Razor file position
  - `razor_stop` — stop the Razor language server to release file locks
- `mcp_stop` tool — stops all LSP child processes (csharp-ls and Roslyn LS) and shuts down the server process; useful for releasing workspace file locks. Note: Claude Code auto-restarts the server, so use `/mcp` disconnect to unlock the server DLL for a source rebuild
- `add-to-project.ps1` — PowerShell script to register csharp-lsp-mcp with a Claude Code project by editing `~/.claude.json`; uses the global tool if installed, falls back to the local debug build
- `csharp_set_workspace` now starts the Roslyn Language Server in the background so Razor tools are ready without extra warm-up time
- `LspClient.WorkspacePath` property so Razor tools reuse the same filtered solution workspace

### Changed
- Switched Razor backend from standalone `rzls` to `Microsoft.CodeAnalysis.LanguageServer` (ships with the VS Code C# extension), which supports both C# and Razor in one process
- `SendRequestAsync` per-request timeout increased from 2 minutes to 5 minutes to accommodate large solution initialization

### Fixed
- Added required `--stdio` flag when launching `Microsoft.CodeAnalysis.LanguageServer`; without it the process exited immediately and all Razor tool calls timed out
- `LspProcessClient.ProcessMessage` now responds to server-initiated JSON-RPC requests (messages with both `id` and `method`) instead of silently dropping them, preventing potential initialization deadlocks
- `LspProcessClient.ProcessMessage` uses `TryGetInt32` instead of `GetInt32` for response IDs, avoiding a crash on non-integer IDs
- Roslyn LS extension discovery now uses semantic version sorting so the latest installed version is always preferred
- `razor_definition` returns a descriptive message when no result is found instead of an empty response
- Fixed concurrent `PublishDiagnostics` waiter overwrite in `RazorClient`
- Atomic version increment for LSP document sync in `RazorClient`
- `FindProjectRoot` in `RazorTools` guards against inaccessible directories (ACL errors)
- Roslyn LS process is now restarted automatically if it exits unexpectedly

## [1.0.0] - 2025-12-11

### Added
- Initial release of csharp-lsp-mcp
- C# language intelligence via csharp-ls integration
  - `csharp_set_workspace` - Set the workspace/solution directory
  - `csharp_diagnostics` - Get compiler errors and warnings
  - `csharp_hover` - Get type information at a position
  - `csharp_completions` - Get IntelliSense completions
  - `csharp_definition` - Go to definition
  - `csharp_references` - Find all references
  - `csharp_symbols` - Get document symbols
  - `csharp_code_actions` - Get available code actions
  - `csharp_rename` - Preview symbol rename
- Built-in XAML analysis tools
  - `xaml_validate` - Validate XAML for errors and issues
  - `xaml_bindings` - Extract and analyze data bindings
  - `xaml_resources` - List and check resource references
  - `xaml_names` - List x:Name declarations
  - `xaml_structure` - Show element tree structure
  - `xaml_find_binding_errors` - Find binding errors
  - `xaml_extract_viewmodel` - Generate ViewModel from bindings
- Support for MCP protocol version 2024-11-05
- Compatible with Claude Code and Claude Desktop
- Verbose logging support via `--verbose` flag or `MCP_DEBUG=1` environment variable

### Dependencies
- ModelContextProtocol 0.5.0-preview.1
- .NET 8.0
- csharp-ls (external dependency)

[Unreleased]: https://github.com/BorisShteynberg/csharp-lsp-mcp/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/BorisShteynberg/csharp-lsp-mcp/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/BorisShteynberg/csharp-lsp-mcp/releases/tag/v1.0.0
