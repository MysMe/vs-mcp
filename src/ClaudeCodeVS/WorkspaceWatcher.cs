using System;
using System.IO;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeCodeVs;

/// <summary>
/// Keeps the lockfile's workspaceFolders (what the shim matches the CLI's cwd against) and the status
/// popup's project name in sync with what's open in VS. The bridge starts at shell-init, before any
/// solution/folder is open, so this picks up solution opens (IVsSolutionEvents), Open-Folder mode
/// (IVsSolutionEvents7), and closes. All callbacks fire on the UI thread.
/// </summary>
internal sealed class WorkspaceWatcher : IVsSolutionEvents, IVsSolutionEvents7, IDisposable
{
    private readonly IVsSolution _solution;
    private readonly Lockfile _lockfile;
    private uint _cookie;

    public WorkspaceWatcher(IVsSolution solution, Lockfile lockfile)
    {
        _solution = solution;
        _lockfile = lockfile;
    }

    public void Start()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _solution.AdviseSolutionEvents(this, out _cookie);
        RefreshFromSolutionInfo(); // pick up a solution/folder already open at load time
    }

    private void RefreshFromSolutionInfo()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_solution.GetSolutionInfo(out string dir, out string file, out _) == VSConstants.S_OK && !string.IsNullOrEmpty(dir))
            SetWorkspace(dir, string.IsNullOrEmpty(file) ? null : Path.GetFileNameWithoutExtension(file));
    }

    private void SetWorkspace(string dir, string? name)
    {
        var root = dir.TrimEnd('\\');
        if (string.Equals(Ui.BridgeStatus.Workspace, root, StringComparison.OrdinalIgnoreCase)
            && (name is null || name == Ui.BridgeStatus.WorkspaceName))
            return; // VS re-raised an event for the workspace we're already on
        _lockfile.UpdateWorkspaceFolders(new[] { root });
        Ui.BridgeStatus.SetWorkspace(root, name ?? Path.GetFileName(root));
    }

    private void ClearWorkspace()
    {
        if (Ui.BridgeStatus.Workspace is null) return;
        _lockfile.UpdateWorkspaceFolders(Array.Empty<string>());
        Ui.BridgeStatus.SetWorkspace(null, null);
    }

    // ---- IVsSolutionEvents (classic solutions) ----
    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // VS raises solution events on the UI thread
        RefreshFromSolutionInfo();
        return VSConstants.S_OK;
    }
    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;
    public int OnAfterCloseSolution(object pUnkReserved)
    {
        ClearWorkspace();
        return VSConstants.S_OK;
    }

    // ---- IVsSolutionEvents7 (Open Folder mode) ----
    public void OnAfterOpenFolder(string folderPath) => SetWorkspace(folderPath, null);
    public void OnBeforeCloseFolder(string folderPath) { }
    public void OnQueryCloseFolder(string folderPath, ref int pfCancel) { }
    public void OnAfterCloseFolder(string folderPath) => ClearWorkspace();
    public void OnAfterLoadAllDeferredProjects() { }

    public void Dispose()
    {
        if (_cookie == 0) return;
        if (!ThreadHelper.CheckAccess()) return; // off the UI thread: skip (VS tears these down anyway)
        // CheckAccess above guarantees the UI thread here; VSTHRD010 can't see that.
#pragma warning disable VSTHRD010
        _solution.UnadviseSolutionEvents(_cookie);
#pragma warning restore VSTHRD010
        _cookie = 0;
    }
}
