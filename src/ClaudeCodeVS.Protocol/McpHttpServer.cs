using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// Localhost-only HTTP endpoint (HttpListener) that serves the vs-debug MCP server at <c>POST /mcp</c>.
/// The CLI never talks to this directly: it launches <c>vs-debug-shim.ps1</c> as a stdio MCP server, and
/// the shim forwards each JSON-RPC message here (one message per POST). Every request must carry the
/// lockfile's auth token; mismatches get a 401. Requests are handled off the UI thread; tool handlers
/// that touch VS marshal to the main thread themselves.
///
/// Also tracks which shim processes have talked to it (the shim sends its own pid), which is how the
/// status popup tells "a Claude session is attached" apart from "listening, nobody here": a stdio MCP
/// server lives exactly as long as its CLI session, so a live shim pid == a live session.
/// </summary>
public sealed class McpHttpServer
{
    private const string AuthHeader = "x-claude-code-ide-authorization";
    private const string ClientPidHeader = "x-vs-debug-client-pid";

    private readonly int _port;
    private readonly string _authToken;
    private readonly McpServer _mcp;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<int, McpClient> _clients = new();

    public McpHttpServer(int port, string authToken, McpServer mcp)
    {
        _port = port;
        _authToken = authToken;
        _mcp = mcp;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public int Port => _port;

    /// <summary>True while the listener is accepting requests.</summary>
    public bool IsListening { get; private set; }

    /// <summary>When the listener started (UTC), or null if it never started.</summary>
    public DateTime? StartedUtc { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        IsListening = true;
        StartedUtc = DateTime.UtcNow;
        Log.Info($"MCP server listening on http://127.0.0.1:{_port}/mcp");

        using var reg = ct.Register(() => { try { _listener.Stop(); } catch { } });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break; // listener stopped during shutdown
                }
                catch (HttpListenerException e)
                {
                    Log.Warn($"listener error: {e.Message}");
                    break;
                }

                // Handle each request independently so one slow tool call can't block accepts.
                _ = Task.Run(() => HandleContextAsync(ctx, ct), ct);
            }
        }
        finally
        {
            IsListening = false;
        }
    }

    /// <summary>
    /// Shim processes that have talked to this server and are still alive, oldest first. Dead ones are
    /// pruned as a side effect.
    /// </summary>
    public IReadOnlyList<McpClient> LiveClients()
    {
        var live = new List<McpClient>();
        foreach (var kv in _clients)
        {
            if (Lockfile.IsProcessAlive(kv.Key, kv.Value.StartTime)) live.Add(kv.Value);
            else if (_clients.TryRemove(kv.Key, out _)) Log.Info($"Claude session detached (shim pid {kv.Key})");
        }
        live.Sort((a, b) => a.FirstSeenUtc.CompareTo(b.FirstSeenUtc));
        return live;
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var remote = ctx.Request.RemoteEndPoint?.ToString() ?? "?";

        // Auth first. Never log the token.
        var presented = ctx.Request.Headers[AuthHeader];
        if (!string.Equals(presented, _authToken, StringComparison.Ordinal))
        {
            Log.Warn($"401 rejected request from {remote} ({(presented is null ? "no" : "bad")} auth token)");
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }

        if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
            || ctx.Request.Url?.AbsolutePath != "/mcp")
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        TrackClient(ctx.Request.Headers[ClientPidHeader]);

        // One JSON-RPC message in the body, one JSON-RPC message back (or an empty 200 for notifications,
        // where HandleAsync returns null). The stdio shim does the newline framing.
        string? response = null;
        try
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            if (body.Length > 0)
                response = await _mcp.HandleAsync(body, ct);
        }
        catch (Exception e)
        {
            Log.Warn($"/mcp request failed: {e.Message}");
        }

        try
        {
            ctx.Response.StatusCode = 200;
            if (response is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(response);
                // Declare the charset: PS 5.1's Invoke-WebRequest decodes charset-less responses as Latin-1.
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
            }
            else
            {
                ctx.Response.ContentLength64 = 0; // notification / no reply due
            }
            ctx.Response.Close();
        }
        catch { /* client gave up */ }
    }

    private void TrackClient(string? pidHeader)
    {
        if (!int.TryParse(pidHeader, out int pid) || pid <= 0) return; // older/hand-written shim: untracked
        var now = DateTime.UtcNow;
        if (_clients.TryGetValue(pid, out var known))
        {
            known.LastSeenUtc = now;
            return;
        }

        long start = 0;
        try { using var p = Process.GetProcessById(pid); start = Lockfile.SafeStartTime(p); }
        catch { /* gone already, or not ours to inspect - liveness falls back to a bare pid check */ }

        if (_clients.TryAdd(pid, new McpClient(pid, start, now)))
            Log.Info($"Claude session attached (shim pid {pid})");
    }
}

/// <summary>One shim process (= one Claude session) that has talked to the MCP server.</summary>
public sealed class McpClient
{
    public McpClient(int pid, long startTime, DateTime firstSeenUtc)
    {
        Pid = pid;
        StartTime = startTime;
        FirstSeenUtc = firstSeenUtc;
        LastSeenUtc = firstSeenUtc;
    }

    public int Pid { get; }
    public long StartTime { get; }
    public DateTime FirstSeenUtc { get; }
    public DateTime LastSeenUtc { get; set; }
}
