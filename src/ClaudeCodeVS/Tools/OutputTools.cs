using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Editor;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// <c>vs_read_output</c>: reads a pane of the Output window - above all the Debug pane, where an F5'd
/// app's Debug/Trace output, first-chance exception notices and binding failures land and no terminal
/// ever sees them. Ported from upstream's BuildTools.cs (minus vs_build); defaults to the Debug pane here.
/// A read, so ungated.
/// </summary>
internal sealed class VsReadOutputTool : IIdeTool
{
    private const int DefaultTail = 200;
    private const int MaxTail = 5000;
    private const int DefaultMaxChars = 20000;

    public string Name => "vs_read_output";

    public string Description =>
        "Read a pane of Visual Studio's Output window - text the CLI has no other way to see. pane 'debug' "
        + "(default: the debugged app's Debug/Trace/Console output, first-chance exception notices - even for "
        + "exceptions that are caught and swallowed - assembly-binding failures and Hot Reload messages, none "
        + "of which reach a terminal under F5), 'build' (the last build's MSBuild log), 'general', or any other "
        + "pane by name ('Claude Debug MCP' for this extension's own log). Returns the LAST 'tail' lines, so a "
        + "long-running log costs a bounded number of tokens. Pass contains to keep only matching lines.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["pane"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "'debug' (default), 'build', 'general', or any pane's display name. "
                                + "The three aliases are matched by pane id, so they work in a non-English VS.",
            },
            ["tail"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = $"How many lines from the end to return (default {DefaultTail}, max {MaxTail}).",
            },
            ["contains"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "Keep only lines containing this text (case-insensitive), applied within "
                                + "the tail window - raise 'tail' to search further back.",
            },
            ["maxChars"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = $"Hard character cap on the returned text, newest kept (default {DefaultMaxChars}).",
            },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string paneArg = ((string?)args["pane"])?.Trim() is { Length: > 0 } p ? p : "debug";
        int tail = Math.Min(MaxTail, Math.Max(1, (int?)args["tail"] ?? DefaultTail));
        string? contains = (string?)args["contains"];
        int maxChars = Math.Min(200_000, Math.Max(500, (int?)args["maxChars"] ?? DefaultMaxChars));

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var pane = OutputWindowReader.Find(paneArg);
        if (pane == null)
        {
            var available = OutputWindowReader.ListPaneNames();
            Log.Info($"vs_read_output(pane={paneArg}) -> no such pane");
            return new JObject
            {
                ["error"] = $"no Output window pane matching '{paneArg}'",
                ["hint"] = "Visual Studio creates a pane lazily - the Debug pane does not exist until a debug "
                         + "session starts, and the Build pane not until the first build.",
                ["availablePanes"] = new JArray(available.Cast<object>().ToArray()),
            };
        }

        string paneName;
        try { paneName = pane.Name ?? paneArg; } catch { paneName = paneArg; }

        string text = OutputWindowReader.ReadText(pane, 1, tail, out bool truncated, out int totalLines);

        var result = new JObject { ["pane"] = paneName, ["totalLines"] = totalLines };

        if (!string.IsNullOrEmpty(contains))
        {
            var kept = text.Split('\n')
                           .Where(l => l.IndexOf(contains!, StringComparison.OrdinalIgnoreCase) >= 0)
                           .ToList();
            result["filter"] = contains;
            result["matchedLines"] = kept.Count;
            text = string.Join("\n", kept);
        }

        if (text.Length > maxChars)
        {
            text = text.Substring(text.Length - maxChars); // keep the newest end
            truncated = true;
        }

        result["text"] = text;
        result["returnedLines"] = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
        if (truncated) result["truncated"] = true;

        Log.Info($"vs_read_output(pane={paneName}, tail={tail}{(contains != null ? $", contains='{contains}'" : "")}) "
               + $"-> {(int?)result["returnedLines"]} line(s)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}
