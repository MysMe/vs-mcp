using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// The lockfile is how the stdio shim finds this VS instance's MCP endpoint. It lives at
/// <c>~/.claude/vs-debug/&lt;port&gt;.lock</c> - the filename IS the port the HTTP server listens on.
/// Deliberately NOT under <c>~/.claude/ide/</c>: that directory is what the CLI's /ide scans for
/// WebSocket IDE bridges, and this extension doesn't speak the IDE WebSocket protocol.
/// </summary>
public sealed class Lockfile
{
    public int Port { get; }
    public string AuthToken { get; }
    public string Path { get; }

    private readonly LockfileDoc _doc;

    private Lockfile(int port, string authToken, string path, LockfileDoc doc)
    {
        Port = port;
        AuthToken = authToken;
        Path = path;
        _doc = doc;
    }

    /// <summary>Where lockfiles (and the shim script) live: <c>~/.claude/vs-debug</c>.</summary>
    public static string BridgeDir =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "vs-debug");

    /// <summary>
    /// Pick a free loopback port, then write the lockfile for it. We bind a throwaway
    /// TcpListener to port 0 (OS assigns a free port), read the assignment, and release it.
    /// There's a tiny TOCTOU window before the HTTP server grabs the port - acceptable in practice.
    /// </summary>
    public static Lockfile CreateForFreePort(IReadOnlyList<string> workspaceFolders)
    {
        Directory.CreateDirectory(BridgeDir);

        int port = PickFreePort();
        var token = Guid.NewGuid().ToString();
        var path = System.IO.Path.Combine(BridgeDir, $"{port}.lock");

        var self = Process.GetCurrentProcess();
        var doc = new LockfileDoc
        {
            Pid = self.Id,
            PidStartTime = SafeStartTime(self),
            WorkspaceFolders = workspaceFolders.ToArray(),
            AuthToken = token,
        };

        File.WriteAllText(path, JsonConvert.SerializeObject(doc, Formatting.Indented));
        Log.Info($"wrote lockfile {path} (pid={doc.Pid}, token=<redacted>)");
        return new Lockfile(port, token, path, doc);
    }

    /// <summary>
    /// Rewrite the lockfile with new workspace folders (same port + token). The bridge starts at
    /// shell-init before any solution is open, so workspaceFolders is initially empty; call this when
    /// a solution/folder opens so the shim can match it against the CLI's working directory.
    /// </summary>
    public void UpdateWorkspaceFolders(IReadOnlyList<string> folders)
    {
        _doc.WorkspaceFolders = folders.ToArray();
        try
        {
            File.WriteAllText(Path, JsonConvert.SerializeObject(_doc, Formatting.Indented));
            Log.Info($"updated lockfile workspaceFolders: {string.Join("; ", _doc.WorkspaceFolders)}");
        }
        catch (Exception e)
        {
            Log.Warn($"could not update lockfile workspaceFolders: {e.Message}");
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
                Log.Info($"deleted lockfile {Path}");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"could not delete lockfile {Path}: {e.Message}");
        }
    }

    /// <summary>
    /// On startup, remove lockfiles whose owning process is dead, so the shim never picks a dead port.
    /// We ONLY delete dead-PID files - never another live VS instance's.
    /// </summary>
    public static void ReapStale()
    {
        if (!Directory.Exists(BridgeDir)) return;

        foreach (var file in Directory.EnumerateFiles(BridgeDir, "*.lock"))
        {
            try
            {
                var doc = JsonConvert.DeserializeObject<LockfileDoc>(File.ReadAllText(file));
                if (doc is null) continue;

                if (!IsProcessAlive(doc.Pid, doc.PidStartTime))
                {
                    File.Delete(file);
                    Log.Info($"reaped stale lockfile {System.IO.Path.GetFileName(file)} (dead/recycled pid {doc.Pid})");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"skipping unreadable lockfile {System.IO.Path.GetFileName(file)}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// True if the process is still alive. Beyond a bare PID check, when a start time is known we verify
    /// the live PID's start time matches - so a recycled PID (Windows reuses the PIDs of dead processes) is
    /// correctly treated as dead. If the start time can't be read (access denied), assume alive.
    /// </summary>
    public static bool IsProcessAlive(int pid, long startTimeFileTimeUtc)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid); // throws ArgumentException if no such process
            if (proc.HasExited) return false;
            if (startTimeFileTimeUtc == 0) return true;
            try { return proc.StartTime.ToFileTimeUtc() == startTimeFileTimeUtc; }
            catch { return true; }                        // can't introspect -> don't risk a false "dead"
        }
        catch (ArgumentException)
        {
            return false; // no process with this PID -> definitely dead
        }
        catch (InvalidOperationException)
        {
            return false; // exited between lookup and query
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // exists but we can't query it
        }
    }

    /// <summary>Process start time as a UTC file-time, or 0 if unavailable.</summary>
    public static long SafeStartTime(Process proc)
    {
        try { return proc.StartTime.ToFileTimeUtc(); }
        catch { return 0; }
    }

    private static int PickFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }
        finally
        {
            l.Stop();
        }
    }

    /// <summary>Exact on-disk schema, read by vs-debug-shim.ps1. Do not rename properties casually.</summary>
    private sealed class LockfileDoc
    {
        [JsonProperty("pid")] public int Pid { get; set; }
        // Paired with Pid to defeat PID reuse when reaping stale lockfiles.
        [JsonProperty("pidStartTime")] public long PidStartTime { get; set; }
        [JsonProperty("workspaceFolders")] public string[] WorkspaceFolders { get; set; } = Array.Empty<string>();
        [JsonProperty("authToken")] public string AuthToken { get; set; } = "";
    }
}
