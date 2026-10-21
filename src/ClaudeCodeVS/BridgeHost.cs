using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using ClaudeCodeVs.Tools;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs;

/// <summary>
/// Owns the bridge's runtime: the output-pane logger, the lockfile, the vs-debug tool registry, and the
/// localhost MCP HTTP server. The accept loop runs on a background task; tool handlers marshal to the UI
/// thread themselves where needed.
/// </summary>
internal sealed class BridgeHost : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private VsOutputLog? _log;
    private Lockfile? _lockfile;
    private McpHttpServer? _server;
    private WorkspaceWatcher? _watcher;
    private Debugging.DebuggerDriver? _driver;             // drives the debugger (continue/step/breakpoints)
    private Debugging.DataBreakpointBridge? _dataBpBridge; // managed data breakpoints (Concord component bridge)

    /// <summary>The MCP server, or null if startup failed before it was created.</summary>
    public McpHttpServer? Server => _server;

    /// <summary>Bring the "Claude Debug MCP" Output pane forward. UI thread.</summary>
    public void ShowLog() => _log?.Activate();

    public async Task StartAsync(CancellationToken ct)
    {
        // 1) Logging first, so everything below is visible.
        _log = await VsOutputLog.CreateAsync(AsyncServiceProvider.GlobalProvider);
        var pane = _log;
        Log.Sink = (level, msg) =>
        {
            if (level == LogLevel.Frame) return; // no per-message JSON noise
            pane.WriteLine(level, msg);
        };
        Log.Info("vs-debug MCP bridge starting…");

        // 2) The shim the CLI launches lives at a stable path, refreshed every start so fixes ship.
        Hooks.McpInstaller.EnsureShim();

        // 3) Lockfile lifecycle: reap stale dead-PID files, then claim a free port.
        Lockfile.ReapStale();
        _lockfile = Lockfile.CreateForFreePort(Array.Empty<string>()); // WorkspaceWatcher fills folders in

        // 4) The debugger tools, served at POST /mcp. The driver owns the IVsDebugger event subscription +
        //    the await-next-break coordination for the drive tools; the data-breakpoint bridge tails the
        //    Concord component's change stream and needs the driver for stop-on-change.
        _driver = new Debugging.DebuggerDriver();
        _dataBpBridge = new Debugging.DataBreakpointBridge(_driver);
        var mcp = new McpServer(new ToolRegistry(BuildDebugTools(_driver, _dataBpBridge)), ServerInstructions);
        mcp.ToolCalled += Ui.BridgeStatus.RecordToolCall;
        _server = new McpHttpServer(_lockfile.Port, _lockfile.AuthToken, mcp);

        // Run the accept loop in the background. If it ever faults (not a normal shutdown), delete the
        // lockfile so the shim doesn't keep finding a dead bridge.
        var server = _server;
        _ = Task.Run(async () =>
        {
            try { await server.RunAsync(_cts.Token); }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception e)
            {
                Log.Error($"MCP server stopped unexpectedly: {e.Message}");
                _lockfile?.Delete();
            }
        }, _cts.Token);

        // 5) Keep the lockfile's workspaceFolders in sync as solutions/folders open, so the shim can pick
        //    the right VS when several are running.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var sol = (IVsSolution?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSolution));
        if (sol != null)
        {
            _watcher = new WorkspaceWatcher(sol, _lockfile);
            _watcher.Start();
        }

        Log.Info($"Bridge ready on port {_lockfile.Port}. Claude sessions reach it through the '{Hooks.McpInstaller.ServerName}' MCP server.");
        if (!Hooks.McpInstaller.IsRegistered())
            Log.Info($"'{Hooks.McpInstaller.ServerName}' isn't registered with Claude yet - use Tools > Claude Debug MCP Status > Register, or run: {Hooks.McpInstaller.RegisterCommand}");
    }

    /// <summary>
    /// Sent to the CLI at connect time and kept in the model's context for the session. Stands in for
    /// upstream's per-prompt break-state hook: instead of pushing the paused state into every prompt (a
    /// PowerShell start per prompt, in every project), tell the model when to go and look.
    /// </summary>
    private const string ServerInstructions =
        "These tools read and drive the user's live Visual Studio debugger (managed .NET). "
        + "When the user mentions a breakpoint, being paused or stopped, a crash, an exception, a hang, or "
        + "asks about runtime values, call vs_debug_state first: it returns the stop location, call stack "
        + "and current locals, or the mode if not paused. Prefer that ground truth over inferring runtime "
        + "behaviour from source. If the app misbehaves silently or an exception seems to vanish, read the "
        + "Debug output pane with vs_read_output - first-chance exceptions and Debug/Trace output land there "
        + "and never reach a terminal. Drive tools may be switched off by the user; if one refuses, say so "
        + "rather than retrying.";

    /// <summary>
    /// The vs-debug tool set. Reads are ungated; drives (and arming data breakpoints) check
    /// <see cref="Ui.BridgeStatus.AllowDebuggerDrive"/> themselves.
    /// </summary>
    private static IEnumerable<IIdeTool> BuildDebugTools(Debugging.DebuggerDriver driver, Debugging.DataBreakpointBridge dataBp)
    {
        // Read / pull (ungated).
        yield return new VsDebugStateTool();
        yield return new VsListBreakpointsTool();
        yield return new VsGetFrameLocalsTool();
        yield return new VsEvaluateTool();
        yield return new VsExpandTool();         // object-graph expansion
        yield return new VsThreadsTool();        // all threads + stacks
        yield return new VsExceptionTool();      // inspect $exception at a throw / in a catch
        yield return new VsListProcessesTool();  // attach targets (debug real running apps)
        yield return new VsReadOutputTool();     // Output window panes, above all Debug (invisible to a terminal)
        yield return new VsWaitChainsTool();     // ClrMD snapshot: structured lock ownership + deadlock suspects
        yield return new VsAsyncStacksTool();    // ClrMD snapshot: logical async call-stack reconstruction
        yield return new VsHeapStatsTool();      // ClrMD snapshot: heap composition + GC/handle/finalizer health
        yield return new VsThreadPoolTool();     // ClrMD snapshot: threadpool counts + starvation
        yield return new VsGcRootsTool();        // ClrMD snapshot: retention path (why is X alive)
        yield return new VsHeapDiffTool();       // ClrMD snapshot: leak finder (baseline vs now)
        // Drive (each gated behind BridgeStatus.AllowDebuggerDrive).
        yield return new VsContinueTool(driver);
        yield return new VsStepOverTool(driver);
        yield return new VsStepIntoTool(driver);
        yield return new VsStepOutTool(driver);
        yield return new VsRunToLineTool(driver);
        yield return new VsBreakAllTool(driver);          // pause a running/hung debuggee (Break All)
        yield return new VsSetBreakpointTool(driver);
        yield return new VsRemoveBreakpointTool(driver);
        yield return new VsBreakOnThrownTool(driver);     // first-chance break at a managed exception's throw site
        yield return new VsFreezeThreadTool(driver);      // freeze/thaw a thread
        yield return new VsSetNextStatementTool(driver);  // move the execution pointer
        // Session control (start = F5 to first break, stop = Shift+F5) + attach to a running app.
        yield return new VsStartDebuggingTool(driver);
        yield return new VsStopDebuggingTool(driver);
        yield return new VsAttachTool(driver);
        yield return new VsDetachTool(driver);
        // Managed data breakpoints (Concord component + file-IPC bridge): watch an instance field,
        // stream every change, optionally break-on-change. vs_set_* is gated drive; vs_get_* is read.
        yield return new VsSetDataBreakpointTool(dataBp);
        yield return new VsGetDataChangesTool(dataBp);
        yield return new VsRemoveDataBreakpointTool(dataBp);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* shutting down */ }
        _watcher?.Dispose();
        _driver?.Dispose();       // unadvise the IVsDebugger event sink (best-effort)
        _dataBpBridge?.Dispose(); // stop the data-breakpoint change tailer
        _lockfile?.Delete();
        _cts.Dispose();
    }
}
