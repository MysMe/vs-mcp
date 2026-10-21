using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeCodeVs.Hooks;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVs.Ui;

/// <summary>
/// The one piece of UI: a small modeless popup (Tools > Claude Debug MCP Status) saying what the bridge is
/// doing - project, port, start time, and whether a Claude session is attached - plus the drive kill
/// switch and the one-time Claude registration. Refreshes itself every second while open. Built in code
/// (no XAML); derives from VS's <see cref="DialogWindow"/> and brushes from VS theme colors so it
/// follows dark/light. UI thread only.
/// </summary>
internal sealed class StatusDialog : DialogWindow
{
    private static StatusDialog? _open;

    private readonly BridgeHost? _host;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _headline = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _started = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _lastCall = new() { Margin = new Thickness(0, 4, 0, 0), Opacity = 0.8 };
    private readonly CheckBox _allowDrive = new() { Content = "Allow Claude to drive the debugger (step, breakpoints, start/stop, attach)", Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _registration = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _register = new() { Content = "Register", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };

    public static void ShowOrActivate(BridgeHost? host)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new StatusDialog(host);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private StatusDialog(BridgeHost? host)
    {
        _host = host;
        Title = "Claude Debug MCP";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current?.MainWindow;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        HasMinimizeButton = false;
        HasMaximizeButton = false;

        SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(_headline);
        root.Children.Add(_started);

        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        stateRow.Children.Add(_dot);
        stateRow.Children.Add(_state);
        root.Children.Add(stateRow);
        root.Children.Add(_lastCall);

        _allowDrive.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _allowDrive.IsChecked = BridgeStatus.AllowDebuggerDrive;
        _allowDrive.Checked += (_, _) => BridgeStatus.SetAllowDebuggerDrive(true);
        _allowDrive.Unchecked += (_, _) => BridgeStatus.SetAllowDebuggerDrive(false);
        root.Children.Add(_allowDrive);

        var regRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        DockPanel.SetDock(_register, Dock.Right);
        // Intentional fire-and-forget (FileAndForget reports faults to the activity log).
#pragma warning disable VSSDK007
        _register.Click += (_, _) => ThreadHelper.JoinableTaskFactory.RunAsync(RegisterAsync).FileAndForget("vsdebugmcp/register");
#pragma warning restore VSSDK007
        regRow.Children.Add(_register);
        regRow.Children.Add(_registration);
        root.Children.Add(regRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var log = new Button { Content = "Show log", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 2, 8, 2) };
        log.Click += (_, _) => { ThreadHelper.ThrowIfNotOnUIThread(); _host?.ShowLog(); };
        var close = new Button { Content = "Close", MinWidth = 80, IsCancel = true, Padding = new Thickness(8, 2, 8, 2) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(log);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = root;

        RefreshRegistration();
        Refresh();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var server = _host?.Server;
        string project = BridgeStatus.WorkspaceName ?? "(no solution open)";

        if (server is null || !server.IsListening)
        {
            _headline.Text = $"MCP server for {project} is not running";
            _started.Text = server?.StartedUtc is DateTime s ? $"Started {FormatTime(s)}, then stopped - see the log" : "It failed to start - see the log";
            SetState(Colors.IndianRed, "Offline");
            _lastCall.Text = "";
            return;
        }

        _headline.Text = $"Running MCP for {project} on port {server.Port}";
        _started.Text = server.StartedUtc is DateTime started ? $"Started {FormatTime(started)} ({FormatAgo(started)})" : "";

        var clients = server.LiveClients();
        if (clients.Count > 0)
            SetState(Colors.MediumSeaGreen, clients.Count == 1
                ? "Connected - 1 Claude session"
                : $"Connected - {clients.Count} Claude sessions");
        else
            SetState(Colors.Goldenrod, "Idle - no Claude session attached");

        _lastCall.Text = BridgeStatus.LastToolUtc is DateTime last
            ? $"Last tool call: {BridgeStatus.LastTool} at {FormatTime(last)} · {BridgeStatus.DebugInspects} read(s), {BridgeStatus.DebugDrives} drive(s)"
            : "No tool calls yet";
    }

    private void SetState(Color color, string text)
    {
        _dot.Fill = new SolidColorBrush(color);
        _state.Text = text;
    }

    private void RefreshRegistration()
    {
        bool registered = McpInstaller.IsRegistered();
        _registration.Text = registered
            ? $"Registered with Claude as '{McpInstaller.ServerName}' (user scope)"
            : $"Not registered with Claude yet - Register runs: {McpInstaller.RegisterCommand}";
        _register.Visibility = registered ? Visibility.Collapsed : Visibility.Visible;
        _register.ToolTip = McpInstaller.RegisterCommand;
    }

    private async System.Threading.Tasks.Task RegisterAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _register.IsEnabled = false;
        try
        {
            McpInstaller.EnsureShim();
            var (ok, output) = await McpInstaller.RegisterAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            RefreshRegistration();
            if (!ok)
                _registration.Text = "Registration failed: " + (output.Length > 0 ? output : "unknown error");
        }
        catch (Exception ex)
        {
            _registration.Text = "Registration failed: " + ex.Message;
        }
        finally
        {
            _register.IsEnabled = true;
        }
    }

    private static string FormatTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm:ss") : local.ToString("yyyy-MM-dd HH:mm");
    }

    private static string FormatAgo(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m ago";
        return $"{(int)span.TotalDays}d {span.Hours}h ago";
    }
}
