# Claude Debug MCP for Visual Studio

A lean fork of [firish/claude_code_vs](https://github.com/firish/claude_code_vs) that keeps **only the debugger**. It's a Visual Studio 2026 extension that exposes the live VS debugger to the `claude` CLI as an MCP server named `vs-debug`. Claude can read your program's runtime state (stack, locals, threads, exceptions, heap) and drive the debugger (step, set breakpoints, attach, break on throw, data breakpoints).

Everything else from upstream is gone: no diff window, panel, notifications, screen capture, attachments, test runner, Roslyn navigation, or IDE-protocol WebSocket. The only UI is a status popup.

**Status:** personal fork, unofficial, not affiliated with Anthropic. VS 2026 only.

## Setup

1. Build the VSIX (below) and install it. **Uninstall the upstream "Claude Code for Visual Studio" extension first**: both ship the same assemblies and data-breakpoint component.
2. Start Visual Studio. The extension starts a localhost MCP server in the background.
3. Register it with Claude once: **Tools > Claude Debug MCP Status... > Register**. That runs:

   ```cmd
   claude mcp add --scope user vs-debug -- powershell -NoProfile -ExecutionPolicy Bypass -File "%USERPROFILE%\.claude\vs-debug\vs-debug-shim.ps1"
   ```

   You can run it yourself from `cmd` instead. The shim file is written by the extension on startup.
4. Run `claude` anywhere. `/mcp` should list `vs-debug` as connected, with tools named `mcp__vs-debug__vs_*`.

You **don't need `/ide`**. `/ide` connects Claude Code's IDE WebSocket, which only carries diffs, selection and diagnostics, and this fork doesn't implement it. The debugger tools reach Claude through the `vs-debug` MCP server, which any `claude` session loads at startup.

If you previously used upstream, remove its `vs-debug` / `vs-semantic` entries from your solutions' `.mcp.json` (and its hooks from `.claude/settings.json`). A project-scope `vs-debug` overrides the user-scope one and points at upstream's shim.

## Status popup

**Tools > Claude Debug MCP Status...** opens a small window that refreshes every second:

```
Running MCP for MySolution on port 51234
Started 10:28:03 (2h 5m ago)
● Connected - 1 Claude session          (or: Idle - no Claude session attached / Offline)
Last tool call: vs_step_over at 12:30:01 · 14 read(s), 3 drive(s)
[x] Allow Claude to drive the debugger (step, breakpoints, start/stop, attach)
Registered with Claude as 'vs-debug' (user scope)
                                                    [Show log] [Close]
```

- **Connected** means at least one live `claude` session has the shim attached to this VS. Each session runs its own shim process, which lives exactly as long as the session.
- **Idle** means the server is listening but no session is attached.
- **Offline** means the server failed to start or stopped. **Show log** opens the *Claude Debug MCP* Output pane.
- **Allow Claude to drive the debugger** is on by default and remembered. Claude Code's own per-tool permission prompt is still the main gate; this is a kill switch. Read tools are never gated.

## Tools

Reads: `vs_debug_state`, `vs_list_breakpoints`, `vs_get_frame_locals`, `vs_evaluate`, `vs_expand`, `vs_threads`, `vs_exception`, `vs_list_processes`, plus ClrMD snapshots `vs_wait_chains`, `vs_async_stacks`, `vs_heap_stats`, `vs_threadpool`, `vs_gc_roots`, `vs_heap_diff`.

Drive: `vs_continue`, `vs_step_over/into/out`, `vs_run_to_line`, `vs_break_all`, `vs_set/remove_breakpoint`, `vs_break_on_thrown`, `vs_freeze_thread`, `vs_set_next_statement`, `vs_start/stop_debugging`, `vs_attach/detach`, and data breakpoints `vs_set_data_breakpoint` / `vs_get_data_changes` / `vs_remove_data_breakpoint`.

The full reference, worked examples and limitations are in [`docs/DEBUGGER.md`](docs/DEBUGGER.md). Runnable fixtures live in [`demo/`](demo).

## How it works

```
claude --stdio--> ~/.claude/vs-debug/vs-debug-shim.ps1 --HTTP POST /mcp--> VS extension (127.0.0.1:<port>)
```

- On startup the extension writes `~/.claude/vs-debug/<port>.lock` (pid, open solution folder, auth token) and listens on `127.0.0.1:<port>`.
- The shim picks the VS instance whose open solution best matches the CLI's working directory (any running VS as a fallback) and forwards each JSON-RPC message, with the token.
- Tools run in-proc against EnvDTE / `IVsDebugger`. The ClrMD tools shell out to a bundled `ClrMdWorker.exe`, and data breakpoints go through a bundled Concord debug-engine component.

The lockfile directory is deliberately not `~/.claude/ide/`, so `/ide` won't offer this VS as an IDE to connect to.

## Build

Requires Visual Studio 2026 with the *Visual Studio extension development* workload.

```powershell
msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /t:Restore,Rebuild /p:Configuration=Release
# -> src/ClaudeCodeVS/bin/Release/ClaudeCodeVS.vsix
```

To debug the extension, press F5 or run `devenv /rootsuffix Exp`, which loads it in the experimental instance.

## Pulling upstream debugger fixes

The kept debugger files sit at the same paths as upstream, and their tools still call `Ui.BridgeStatus` as upstream does, so upstream fixes merge mostly cleanly:

```powershell
git fetch upstream
git merge upstream/main   # resolve modify/delete conflicts by keeping the deletion
```

## License

[MIT](LICENSE). Original work © 2026 Rishi Gulati. "Claude" and "Claude Code" are trademarks of Anthropic, used only to describe interoperability.
