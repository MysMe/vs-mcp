using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVs;

/// <summary>
/// VS package entry point. Auto-loads in the background when the shell finishes initializing (so the MCP
/// server is up whether or not a solution is open) and registers the one command: Tools > Claude Debug
/// MCP Status.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuids.PackageString)]
// Register the extension folder as an assembly probe path. The package assembly isn't strong-named, so
// the generated pkgdef uses "Assembly=" with no CodeBase; without a binding path devenv can't locate
// ClaudeCodeVS.dll / ClaudeCodeVS.Protocol.dll and the package fails to load.
[ProvideBindingPath]
[ProvideMenuResource("Menus.ctmenu", 1)] // the compiled VSCommandTable.vsct
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class ClaudeCodeVsPackage : AsyncPackage
{
    private BridgeHost? _host;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Ui.BridgeStatus.LoadSettings();
        await TaskScheduler.Default; // hop back off the UI thread for server startup

        _host = new BridgeHost();
        try
        {
            await _host.StartAsync(cancellationToken);
        }
        catch (Exception e)
        {
            // Leave the package (and its status command) alive so the popup can report "offline".
            Protocol.Log.Error($"bridge failed to start: {e.Message}");
        }

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
        {
            mcs.AddCommand(new MenuCommand(OnShowStatus,
                new CommandID(PackageGuids.CommandSet, PackageIds.ShowStatus)));
        }
    }

    private void OnShowStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Ui.StatusDialog.ShowOrActivate(_host);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _host?.Dispose();
        base.Dispose(disposing);
    }
}

internal static class PackageGuids
{
    public const string PackageString = "798e095b-5e74-4a07-bdf0-500d897ef746";
    // Must match guidVsDebugMcpCmdSet in VSCommandTable.vsct.
    public static readonly Guid CommandSet = new Guid("539df883-955a-4653-b639-0999a0300b40");
}

internal static class PackageIds
{
    // Must match the IDSymbol values in VSCommandTable.vsct.
    public const int ShowStatus = 0x0100;
}
