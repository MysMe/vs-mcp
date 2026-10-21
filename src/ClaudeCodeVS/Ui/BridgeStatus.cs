using System;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// The little bit of shared state the status popup shows: which workspace is open, the drive gate, and
/// what Claude last did. Kept under the upstream name/namespace so the debugger tools (which call
/// <see cref="RecordDebugInspect"/> etc.) stay byte-identical to upstream and keep merging cleanly.
/// Thread-safe enough for its job: plain field writes from tool threads, reads from the UI timer.
/// </summary>
internal static class BridgeStatus
{
    private const string SettingsCollection = "VsDebugMcp";
    private const string AllowDriveKey = "AllowDebuggerDrive";

    /// <summary>Open solution/folder root, or null.</summary>
    public static string? Workspace { get; private set; }

    /// <summary>Display name of what's open (the .sln/.slnx name, or the folder name), or null.</summary>
    public static string? WorkspaceName { get; private set; }

    public static void SetWorkspace(string? root, string? name)
    {
        Workspace = root;
        WorkspaceName = name;
    }

    /// <summary>
    /// Allow Claude to DRIVE the debugger (continue/step/breakpoints/start/stop/attach/data breakpoints).
    /// Reads are never gated. Default ON - Claude Code's own per-tool permission prompt is the primary
    /// gate; this is a kill switch. Persisted per user in the VS settings store.
    /// </summary>
    public static bool AllowDebuggerDrive { get; private set; } = true;

    public static void SetAllowDebuggerDrive(bool value)
    {
        AllowDebuggerDrive = value;
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = new ShellSettingsManager(ServiceProvider.GlobalProvider)
                .GetWritableSettingsStore(SettingsScope.UserSettings);
            if (!store.CollectionExists(SettingsCollection)) store.CreateCollection(SettingsCollection);
            store.SetBoolean(SettingsCollection, AllowDriveKey, value);
        }
        catch (Exception e)
        {
            Protocol.Log.Warn($"could not persist the drive setting: {e.Message}");
        }
    }

    /// <summary>Load persisted settings. Call once on the UI thread at package init.</summary>
    public static void LoadSettings()
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var store = new ShellSettingsManager(ServiceProvider.GlobalProvider)
                .GetReadOnlySettingsStore(SettingsScope.UserSettings);
            AllowDebuggerDrive = store.GetBoolean(SettingsCollection, AllowDriveKey, true);
        }
        catch (Exception e)
        {
            Protocol.Log.Warn($"could not load settings, using defaults: {e.Message}");
        }
    }

    public static int DebugInspects { get; private set; }
    public static int DebugDrives { get; private set; }

    /// <summary>Record a debugger READ tool call.</summary>
    public static void RecordDebugInspect() => DebugInspects++;

    /// <summary>Record a debugger DRIVE tool call that passed the gate.</summary>
    public static void RecordDebugDrive() => DebugDrives++;

    /// <summary>Most recent tools/call name and when it arrived (UTC).</summary>
    public static string? LastTool { get; private set; }
    public static DateTime? LastToolUtc { get; private set; }

    public static void RecordToolCall(string name)
    {
        LastTool = name;
        LastToolUtc = DateTime.UtcNow;
    }
}
