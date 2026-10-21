using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Puts the stdio shim somewhere stable and registers it with Claude Code as the user-scope MCP server
/// <c>vs-debug</c>. The shim is (re)written to <c>~/.claude/vs-debug/vs-debug-shim.ps1</c> at every startup
/// (the extension's own install folder changes with each version, so it can't be referenced directly).
///
/// Registration is a one-time, explicit step (the status popup's Register button, or the same command
/// by hand) - we never edit <c>~/.claude.json</c> ourselves; the CLI owns that file and rewrites it
/// concurrently. User scope means every Claude session gets the tools without touching any repo.
/// </summary>
internal static class McpInstaller
{
    public const string ServerName = "vs-debug";
    private const string ShimScript = "vs-debug-shim.ps1";

    public static string ShimPath => Path.Combine(Lockfile.BridgeDir, ShimScript);

    /// <summary>The <c>claude mcp add</c> arguments that register the shim (user scope).</summary>
    public static string RegisterArgs =>
        $"mcp add --scope user {ServerName} -- powershell -NoProfile -ExecutionPolicy Bypass -File \"{ShimPath}\"";

    /// <summary>The full command, for copy/paste into cmd or a bash shell.</summary>
    public static string RegisterCommand => "claude " + RegisterArgs;

    /// <summary>Write the embedded shim to its stable location. Best-effort; never throws.</summary>
    public static void EnsureShim()
    {
        try
        {
            Directory.CreateDirectory(Lockfile.BridgeDir);
            ManagedScripts.WriteIfManaged(ShimPath, ReadEmbeddedScript(ShimScript));
        }
        catch (Exception e)
        {
            Log.Warn($"mcp: writing the shim failed: {e.Message}");
        }
    }

    /// <summary>
    /// True if <c>~/.claude.json</c> has a user-scope <c>vs-debug</c> server. Read-only peek; a project
    /// <c>.mcp.json</c> registration isn't detected (and isn't needed).
    /// </summary>
    public static bool IsRegistered()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var root = JObject.Parse(reader.ReadToEnd());
            return root["mcpServers"]?[ServerName] is JObject;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Run <c>claude mcp add</c> (via cmd.exe, so both the native claude.exe and an npm claude.cmd
    /// resolve). Returns (success, combined output). Runs off the UI thread.
    /// </summary>
    public static Task<(bool ok, string output)> RegisterAsync() => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c \"claude " + RegisterArgs + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(); } catch { }
                return (false, "claude mcp add timed out");
            }
            var output = (stdout.Result + stderr.Result).Trim();
            Log.Info($"mcp: `{RegisterCommand}` exited {p.ExitCode}: {output}");
            return (p.ExitCode == 0, output);
        }
        catch (Exception e)
        {
            Log.Warn($"mcp: registration failed: {e.Message}");
            return (false, e.Message);
        }
    });

    private static string ReadEmbeddedScript(string scriptFileName)
    {
        var asm = typeof(McpInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"embedded shim script not found: {scriptFileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
