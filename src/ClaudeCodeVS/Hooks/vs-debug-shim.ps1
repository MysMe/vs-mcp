# vs:auto-managed - the VS debug MCP extension (re)writes this file at startup so fixes ship automatically. Remove THIS LINE to take ownership and the extension will leave this file alone.
# stdio MCP proxy shim for the vs-debug MCP server (registered once with `claude mcp add --scope user`).
#
# The CLI launches this as an ordinary stdio MCP server. The actual debugger tools (vs_debug_state,
# vs_evaluate, vs_step_over, ...) live IN the Visual Studio extension, reachable over its authenticated
# localhost HTTP endpoint (POST /mcp). This script is a dumb pipe: it forwards each JSON-RPC message from
# stdin to that endpoint and writes the reply to stdout.
#
# Why a shim at all (vs. a plain type:http MCP entry)? The port is dynamic per VS instance. The shim
# discovers the LIVE instance at connect time from ~/.claude/vs-debug/<port>.lock, preferring the VS
# whose open solution best matches the CLI's working directory - so it works across multiple VS windows
# and survives stale lockfiles, without rewriting the Claude config on every launch.
#
# Contract: stdout carries ONLY framed JSON-RPC (newline-delimited, one message per line, UTF-8, no BOM).
# Anything else on stdout corrupts the MCP stream, so diagnostics must go to stderr only. Fail-soft:
# if the bridge is unreachable we answer requests with a JSON-RPC error (never hang the CLI) and drop
# notifications.
#
# Every POST carries this process's pid, which is how the extension's status popup knows a Claude
# session is attached (this process lives exactly as long as the session).

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # suppress Invoke-WebRequest's progress bar (it can hit stderr)

$script:Port = $null
$script:Token = $null

function Test-Port([int]$pt) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $live = $c.BeginConnect('127.0.0.1', $pt, $null, $null).AsyncWaitHandle.WaitOne(300) -and $c.Connected
        $c.Close(); return $live
    } catch { return $false }
}

# Find the live Visual Studio bridge: the MOST-SPECIFIC workspace match (longest workspaceFolders prefix
# of this process's cwd - the CLI launches us in its project dir) whose port is actually listening. An
# unrelated VS still ranks (last), so a session started outside any open solution reaches *a* debugger.
# Sets $script:Port and $script:Token; returns $true on success.
function Resolve-Bridge {
    $cwd = (Get-Location).Path
    $lockDir = Join-Path $env:USERPROFILE '.claude\vs-debug'
    $cands = @()
    foreach ($f in Get-ChildItem $lockDir -Filter *.lock -ErrorAction SilentlyContinue) {
        try {
            $j = Get-Content -Raw $f.FullName | ConvertFrom-Json
            $ws = if ($j.workspaceFolders) { [string]$j.workspaceFolders[0] } else { '' }
            # Separator-aware, containment BOTH ways, ranked: exact > session inside workspace >
            # workspace inside session > unrelated. Separator-aware so 'C:\work\app' never matches a
            # session in 'C:\work\app-service'.
            $wsN = ($ws -replace '/', '\').TrimEnd('\'); $cwdN = ([string]$cwd -replace '/', '\').TrimEnd('\')
            $rank = 0
            if ($wsN -and $cwdN) {
                if     ($cwdN -eq $wsN)             { $rank = 3 }
                elseif ($cwdN -like ($wsN + '\*'))  { $rank = 2 }
                elseif ($wsN -like ($cwdN + '\*'))  { $rank = 1 }
            }
            $cands += [pscustomobject]@{ Port = [int]$f.BaseName; Token = $j.authToken; Score = ($rank * 1000000 + $ws.Length) }
        } catch { }
    }
    foreach ($cand in ($cands | Sort-Object Score -Descending)) {
        if (Test-Port $cand.Port) { $script:Port = $cand.Port; $script:Token = $cand.Token; return $true }
    }
    return $false
}

# Forward one JSON-RPC message to the bridge; return the (possibly empty) reply body. Throws on transport
# failure so the caller can re-discover and retry. Decode the reply from RAW BYTES as UTF-8 ourselves:
# PS 5.1's $resp.Content pre-decodes with the response charset, or Latin-1 when none is declared, which
# mangles any non-ASCII in tool output (e.g. a window title's Unicode) into mojibake.
function Send-ToBridge([string]$json) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $resp = Invoke-WebRequest -Uri "http://127.0.0.1:$($script:Port)/mcp" -Method Post `
        -ContentType 'application/json; charset=utf-8' `
        -Headers @{ 'x-claude-code-ide-authorization' = $script:Token; 'x-vs-debug-client-pid' = [string]$PID } `
        -Body $bytes -TimeoutSec 60 -UseBasicParsing
    if ($resp.RawContentStream) {
        return [System.Text.Encoding]::UTF8.GetString($resp.RawContentStream.ToArray())
    }
    return [string]$resp.Content
}

# UTF-8 stdio, no BOM, autoflush so the CLI sees each reply immediately.
$stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)
$stdout = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), (New-Object System.Text.UTF8Encoding($false)))
$stdout.AutoFlush = $true

Resolve-Bridge | Out-Null   # best-effort warm-up; tools/call re-resolves if the bridge moved

while ($null -ne ($line = $stdin.ReadLine())) {
    $line = $line.Trim()
    if (-not $line) { continue }

    # Determine whether a reply is owed (requests have an id; notifications don't) so we can answer with
    # an error if the bridge is down without hanging the CLI on a request.
    $hasId = $false; $id = $null
    try {
        $msg = $line | ConvertFrom-Json
        if ($msg.PSObject.Properties.Name -contains 'id') { $hasId = $true; $id = $msg.id }
    } catch { }

    $response = $null; $ok = $false
    for ($attempt = 0; $attempt -lt 2 -and -not $ok; $attempt++) {
        if (-not $script:Port) { if (-not (Resolve-Bridge)) { break } }
        try { $response = Send-ToBridge $line; $ok = $true }
        catch { $script:Port = $null }   # bridge moved/restarted -> re-resolve on the next attempt
    }

    if (-not $ok) {
        if ($hasId) {
            $err = @{ jsonrpc = '2.0'; id = $id; error = @{ code = -32000; message = 'Visual Studio bridge unavailable' } } |
                ConvertTo-Json -Compress -Depth 6
            $stdout.Write($err); $stdout.Write("`n")
        }
        continue
    }

    if ($response) { $stdout.Write($response); $stdout.Write("`n") }
}
