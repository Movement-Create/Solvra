#nullable enable

using System.Diagnostics;
using System.Text.Json;
using Solvra.Models;
using Solvra.Security;

namespace Solvra.Tools;

public class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly PermissionChecker _permissionChecker = new();
    private readonly AuditLogger? _auditLogger;

    public ToolRegistry(AuditLogger? auditLogger = null)
    {
        _auditLogger = auditLogger;
    }

    public void RegisterTool(ITool tool, bool force = false)
    {
        if (_tools.ContainsKey(tool.Name) && !force)
            throw new InvalidOperationException($"Tool \"{tool.Name}\" is already registered. Use force=true to override.");

        _tools[tool.Name] = tool;
    }

    // Explicit IToolRegistry implementation
    void IToolRegistry.RegisterTool(ITool tool) => RegisterTool(tool);

    public ITool? GetTool(string name)
    {
        return _tools.TryGetValue(name, out var tool) ? tool : null;
    }

    public IReadOnlyList<ITool> GetAllTools() => _tools.Values.ToList();

    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        return _tools.Values.Select(tool =>
        {
            var schemaElement = tool.GetInputSchema();
            var schema = JsonSerializer.Deserialize<ToolInputSchema>(schemaElement.GetRawText())
                         ?? new ToolInputSchema();
            return new ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = schema
            };
        }).ToList();
    }

    public async Task<ToolExecuteResult> ExecuteToolAsync(
        string name,
        JsonElement input,
        ToolExecutionContext context,
        CancellationToken ct = default)
    {
        var tool = GetTool(name);
        if (tool == null)
            return new ToolExecuteResult($"Tool \"{name}\" not found", true);

        if (!IsToolAllowed(name, context.Session))
            return new ToolExecuteResult($"Tool \"{name}\" is not allowed in this session.", true);

        var sw = Stopwatch.StartNew();
        ToolExecuteResult result;
        try
        {
            result = await tool.ExecuteAsync(input, context, ct);
        }
        catch (Exception ex)
        {
            result = new ToolExecuteResult($"Tool \"{name}\" threw an error: {ex.Message}", true);
        }
        sw.Stop();

        if (_auditLogger != null)
        {
            await _auditLogger.LogToolExecutionAsync(context.SessionId, name, result.IsError, sw.ElapsedMilliseconds);
        }

        return result;
    }

    public async Task<ToolExecuteResult> ExecuteToolAsync(
        string name,
        JsonElement input,
        ToolExecutionContext context,
        PermissionMode permissionMode,
        Func<ITool, Task<bool>>? permissionCallback = null,
        CancellationToken ct = default)
    {
        var tool = GetTool(name);
        if (tool == null)
            return new ToolExecuteResult($"Tool \"{name}\" not found", true);

        if (!IsToolAllowed(name, context.Session))
            return new ToolExecuteResult($"Tool \"{name}\" is not allowed in this session.", true);

        var permissionGranted = await _permissionChecker.CheckPermissionAsync(tool, permissionMode, permissionCallback);
        if (!permissionGranted)
            return new ToolExecuteResult($"Tool \"{name}\" requires permission level \"{tool.PermissionLevel}\" which was denied.", true);

        var sw = Stopwatch.StartNew();
        ToolExecuteResult result;
        try
        {
            result = await tool.ExecuteAsync(input, context, ct);
        }
        catch (Exception ex)
        {
            result = new ToolExecuteResult($"Tool \"{name}\" threw an error: {ex.Message}", true);
        }
        sw.Stop();

        if (_auditLogger != null)
        {
            await _auditLogger.LogToolExecutionAsync(context.SessionId, name, result.IsError, sw.ElapsedMilliseconds);
        }

        return result;
    }

    private static bool IsToolAllowed(string name, SessionInfo session)
    {
        if (session.DisallowedTools.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;

        if (session.AllowedTools.Count > 0)
            return session.AllowedTools.Contains(name, StringComparer.OrdinalIgnoreCase);

        return true;
    }

    /// <summary>
    /// Register all built-in tools with default configuration.
    /// </summary>
    public void RegisterBuiltins(SandboxManager sandbox)
    {
        RegisterTool(new BashTool(sandbox));
        RegisterTool(new CodeRunTool(sandbox));
        RegisterTool(new FileReadTool());
        RegisterTool(new FileWriteTool());
        RegisterTool(new FileEditTool());
        RegisterTool(new GlobTool());
        RegisterTool(new GrepTool());
        RegisterTool(new WebFetchTool());
        RegisterTool(new WebSearchTool());
        RegisterTool(new DocCreateTool());
        RegisterTool(new SpreadsheetCreateTool());
        RegisterTool(new CsvWriteTool());
        RegisterTool(new MemoryNoteTool());
        RegisterTool(new MemoryRecallTool());
        RegisterTool(new TodoTool());
        RegisterTool(new AgentTool());
    }
}
