# Razor Pages Support Design

**Date:** 2026-05-27
**Scope:** Add `razor_diagnostics` and `razor_definition` MCP tools for ASP.NET Core Razor Pages (`.cshtml` files) via the Razor Language Server (`rzls`).

---

## Goal

Expose Razor Pages language intelligence — compiler diagnostics and go-to-definition — to AI assistants through two new MCP tools, using `rzls` (Microsoft's Razor Language Server) as the backend.

---

## Architecture

```
Claude (MCP client)
    ├── CSharpTools  ──→  LspClient    ──→  csharp-ls (Roslyn)
    └── RazorTools   ──→  RazorClient  ──→  rzls (Razor LS)
```

Both `LspClient` and `RazorClient` speak LSP over JSON-RPC via process stdin/stdout. To avoid duplicating ~300 lines of shared machinery, a `LspProcessClient` base class is extracted from `LspClient`. Both clients extend it.

### Files

| Action | Path | Responsibility |
|--------|------|----------------|
| Create | `Lsp/LspProcessClient.cs` | Shared LSP base: process lifecycle, JSON-RPC read loop, send/receive, pending requests |
| Modify | `Lsp/LspClient.cs` | Extends `LspProcessClient`; retains all existing C# operations unchanged |
| Create | `Lsp/RazorClient.cs` | Extends `LspProcessClient`; rzls discovery, startup, diagnostics, definition |
| Create | `Tools/RazorTools.cs` | MCP tool handlers: `razor_diagnostics`, `razor_definition` |
| Modify | `Program.cs` | Register `RazorTools` and `RazorClient` with DI |

---

## `LspProcessClient` Base Class

Extracted from `LspClient` — no behavioral changes to existing C# tools:

- Process start/stop/kill
- `IAsyncDisposable` with `Interlocked` idempotency guard (`private int _disposed`)
- JSON-RPC read loop (`ReadLoopAsync`, `ReadContentLengthAsync`, `ReadExactAsync`)
- `SendMessageAsync` / `SendRequestAsync` / `SendNotificationAsync`
- Pending request tracking (`_pendingRequests`, `ProcessMessage`)
- `_initLock`, `_writeLock`, `_readLoopCts` lifecycle

`LspClient` extends `LspProcessClient`, adds only the csharp-ls `initialize` handshake and C# LSP operations. Its public API is unchanged.

---

## `rzls` Discovery

`rzls` ships inside the .NET SDK toolchain at a version-specific path. `FindRazorServerAsync` probes in order:

**Pass 1 — PATH:** Try `rzls --version` unqualified. Covers PATH-based installs and future standalone packaging.

**Pass 2 — SDK-relative:** Run `dotnet --list-sdks`, which outputs lines of the form `8.0.100 [/path/to/sdk/root]`. Parse each line to extract the version string and root path; sort by version descending (newest first). For each entry, probe:
```
<sdk-root>/<version>/DotnetTools/dotnet-razor/tools/net<major>/any/rzls[.exe]
```
where `<major>` is the major version number (e.g., `net8`). The exact sub-path must be verified against the Roslyn SDK package contents at implementation time — use `dotnet --list-sdks` output on a machine with the SDK installed to confirm.

Returns the first path where `--version` exits 0. If nothing found, returns `null` and tools surface: *"rzls not found — install .NET SDK 8.0.3+ or the VS Code C# extension."*

---

## `RazorClient`

Extends `LspProcessClient`. Manages the `rzls` process lifecycle and exposes two operations.

### Startup (`StartAsync`)

1. Discover `rzls` via `FindRazorServerAsync`. Return `false` if not found.
2. Start process with `UseShellExecute = false`, stdio redirected.
3. Send standard LSP `initialize` request with project root URI.
4. Send `razor/initialize` request with Razor-specific parameters (format version, host info — sourced from `github.com/dotnet/vscode-csharp` extension source).
5. If `rzls` requires a live Roslyn delegation connection and initialization fails, return `false` with log message: *"Razor LSP failed to initialize — rzls may require a running Roslyn host."*

Workspace is always auto-discovered from the `filePath` argument: walk up the directory tree from the file until a `.csproj` is found, use its parent directory as the project root. `RazorClient` does not share state with `CSharpTools` or `LspClient`.

### Document Versioning

`RazorClient` maintains a `_openDocuments` dictionary mapping `filePath → version` (same pattern as `CSharpTools`). This drives the open/change/close lifecycle:

- **First call for a file:** send `textDocument/didOpen` with `version = 1`.
- **Subsequent calls with new content:** send `textDocument/didChange` with an incremented version. Do NOT re-send `didOpen` — rzls treats a second `didOpen` for an already-open URI as a protocol error.
- **Content unchanged:** if the caller passes the same content (or omits content and the file on disk is unchanged), skip the notification entirely and let rzls use its cached state.
- **`StopAsync`:** sends `textDocument/didClose` for every tracked document before shutting down, then clears `_openDocuments`.

Version numbers are per-document integers incremented on each `didChange`. They do not need to stay in sync with `LspClient`'s document versions — the two clients track independent sets of open files.

### Operations

**`GetDiagnosticsAsync(filePath, content, cancellationToken)`**
- Opens or updates the document via the versioning rules above.
- Waits up to 10 seconds for a `textDocument/publishDiagnostics` notification.
- Returns diagnostics received by the deadline (empty list if none arrive — not an error).

**`GetDefinitionAsync(filePath, line, character, cancellationToken)`**
- Opens or updates the document via the versioning rules above.
- Sends `textDocument/definition` request.
- Returns `Location[]` or null if rzls returns no result.

### Shutdown

`StopAsync` and `DisposeAsync` follow the same pattern as `LspClient`: `StopAsync` acquires `_initLock`, performs orderly shutdown (LSP `shutdown` + `exit`, process kill fallback), resets state. `DisposeAsync` uses `Interlocked.Exchange` guard + delegates to `StopAsync`.

---

## MCP Tools (`RazorTools`)

### `razor_diagnostics`

```
Parameters:
  filePath  (string, required)  — absolute path to .cshtml file
  content   (string, optional)  — file content; reads from disk if omitted

Returns: array of diagnostics — message, severity, line, character, endLine, endCharacter
```

Auto-starts `RazorClient` on first call. Returns tool error (not exception) if rzls cannot start. Waits up to 10 seconds for diagnostics.

### `razor_definition`

```
Parameters:
  filePath   (string, required)  — absolute path to .cshtml file
  line       (int, required)     — 0-based line number
  character  (int, required)     — 0-based character offset

Returns: target filePath + line + character, or null if no definition at position
```

Auto-starts `RazorClient` on first call. Opens the document if not already open before sending the definition request.

---

## Error Handling

| Condition | Behavior |
|-----------|----------|
| `rzls` not found | Tool returns error string with install guidance; no exception |
| `razor/initialize` fails | `StartAsync` returns `false`; tools return descriptive error message |
| Diagnostics timeout (10s) | Return empty list with log warning — not an error |
| `rzls` crashes mid-session | Detected on next tool call; `StartAsync` restarts the process |
| File not found on disk (no `content` provided) | Return tool error with path |

---

## Testing

### `LspProcessClientTests`

Unit tests for the extracted base using `MemoryStream` (moved from `LspClientTests`, no behavioral change):
- Content-length framing (read/write round-trip)
- Short-stream handling (`ReadExactAsync` returns false)
- Read loop cancellation

### `RazorClientTests`

Unit tests using `MemoryStream` — no real `rzls` process required:
- `razor/initialize` handshake: verify correct request shape is sent
- Diagnostic deserialization: feed a canned `textDocument/publishDiagnostics` JSON payload, assert correct `Diagnostic` objects returned
- Definition response parsing: feed a canned `textDocument/definition` response, assert correct `Location` returned
- Discovery failure: `FindRazorServerAsync` returns null → `StartAsync` returns false

### Not tested

Integration tests against a real `rzls` binary — consistent with the existing approach for `csharp-ls`.

---

## Out of Scope

- Razor completions and hover (not requested)
- Blazor `.razor` components (different format, separate effort)
- `razor_set_workspace` tool (workspace is auto-discovered)
- Razor tag helper resolution (requires full Roslyn delegation; may not work without it)
