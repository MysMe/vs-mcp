using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Protocol;

/// <summary>
/// One handler per MCP tool. In the VSIX each implementation is backed by the VS debugger (EnvDTE,
/// IVsDebugger, the ClrMD worker, the data-breakpoint component). Return a plain string to send it
/// verbatim on the wire; return any other object to have it JSON-stringified; throw to surface an MCP
/// error (isError=true).
/// </summary>
public interface IIdeTool
{
    string Name { get; }

    /// <summary>Human/model-facing description advertised in tools/list - helps the model pick the tool.</summary>
    string Description { get; }

    /// <summary>The JSON Schema advertised in tools/list. Becomes the mcp__vs-debug__* tool the model sees.</summary>
    JToken Schema { get; }

    Task<object> InvokeAsync(JToken arguments, CancellationToken ct);
}

/// <summary>Name-keyed lookup of the registered tools. Built once at startup.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IIdeTool> _tools;

    public ToolRegistry(IEnumerable<IIdeTool> tools)
        => _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IEnumerable<IIdeTool> All => _tools.Values;

    public bool TryGet(string name, out IIdeTool tool) => _tools.TryGetValue(name, out tool!);
}
