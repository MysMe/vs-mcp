# CLAUDE.md

A lean fork of firish/claude_code_vs: a VS 2026 in-proc extension that exposes the live Visual Studio debugger to the `claude` CLI as the MCP server `vs-debug`, and nothing else. User-facing docs: `README.md`, `docs/DEBUGGER.md`.

## Architecture

- `src/ClaudeCodeVS.Protocol/` (pure BCL, net48): `Lockfile` (`~/.claude/vs-debug/<port>.lock`), `McpHttpServer` (HttpListener on 127.0.0.1, token auth, `POST /mcp`, tracks attached shim pids), `McpServer` (JSON-RPC/MCP dispatch), `IIdeTool` + `ToolRegistry`, `Log`.
- `src/ClaudeCodeVS/`
  - `ClaudeCodeVsPackage.cs`: background auto-load at shell init; registers the one command (Tools > Claude Debug MCP Status).
  - `BridgeHost.cs`: wires logger, lockfile, MCP server and tool registry.
  - `WorkspaceWatcher.cs`: keeps the lockfile's `workspaceFolders` and the popup's project name current.
  - `Hooks/McpInstaller.cs` + `Hooks/vs-debug-shim.ps1`: writes the stdio shim to `~/.claude/vs-debug/` on startup; runs `claude mcp add --scope user vs-debug ...` when the user clicks Register. Never edits `~/.claude.json` directly; it only reads it to show registration status.
  - `Ui/StatusDialog.cs`: the modeless status popup. `Ui/BridgeStatus.cs`: shared state and the persisted drive gate. It keeps the upstream name so the tool files stay byte-identical to upstream.
  - `Debugging/` + `Tools/{DebugTools,DriveTools,DataBpTools}.cs`: unchanged from upstream apart from hint text.
- `src/ClaudeCodeVS.ClrMdWorker/`: out-of-proc ClrMD worker (ClrMD can't load in devenv), bundled under `ClrMdWorker\` in the VSIX.
- `src/ClaudeCodeVS.DataBpComponent/`: Concord component for managed data breakpoints (`DebuggerEngineExtension` asset).

## Conventions

1. **Threading.** HTTP handlers run off the UI thread. Anything touching EnvDTE/VS services must `await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct)` first.
2. **Localhost + auth.** Bind 127.0.0.1 only; reject requests without the lockfile token (`x-claude-code-ide-authorization`) with 401. Never log the token.
3. **Shim stdout is JSON-RPC only.** Diagnostics go to stderr. HTTP responses must declare `charset=utf-8` (PS 5.1 decodes charset-less responses as Latin-1).
4. **Shim requests carry `x-vs-debug-client-pid`.** That's how the popup knows a session is attached; keep it if the shim changes.
5. **No IDE WebSocket.** `/ide` is deliberately unsupported. The lockfile stays out of `~/.claude/ide/`, or the CLI would offer this VS as an IDE it can't talk to.

## Build

```powershell
msbuild src/ClaudeCodeVS/ClaudeCodeVS.csproj /t:Restore,Rebuild /p:Configuration=Release
```

Editing `VSCommandTable.vsct` needs `devenv /rootsuffix Exp /updateconfiguration` once before menu changes show in the experimental instance.
