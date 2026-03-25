using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Vivarium;

[McpServerToolType]
public sealed class VivariumTools
{
    private readonly ScriptingEngine _engine;
    private readonly FileStore _fileStore;
    private readonly BootstrapLoader _loader;

    public VivariumTools(ScriptingEngine engine, FileStore fileStore, BootstrapLoader loader)
    {
        _engine = engine;
        _fileStore = fileStore;
        _loader = loader;
    }

    [McpServerTool(Name = "vivarium_eval"), Description(
        "Execute arbitrary C# code in the live Vivarium scripting session. " +
        "Variables, classes, and functions defined in prior eval calls persist. " +
        "Returns stdout, return value (if expression), and any errors.")]
    public async Task<string> Eval(
        [Description("C# code to execute. Can be expressions, statements, class definitions, etc.")]
        string code,
        [Description("Timeout in milliseconds (default 30000)")]
        int timeoutMs = 30000)
    {
        var result = await _engine.EvalAsync(code, timeoutMs);
        return result.ToString();
    }

    [McpServerTool(Name = "vivarium_define"), Description(
        "Create or update a Vivarium source file and execute it in the live session. " +
        "The file is persisted under .vivarium/project/ and survives restarts. " +
        "Use this to build up reusable functions, classes, and utilities.")]
    public async Task<string> Define(
        [Description("Relative path under .vivarium/project/, e.g. 'Utils/Math.cs' or 'Helpers.cs'")]
        string path,
        [Description("Full C# source code. The //@VIVARIUM@ header is auto-added if missing. " +
            "Use //@description: and //@depends: OtherFile.cs metadata comments for organization.")]
        string source)
    {
        var def = _fileStore.Write(path, source);
        var evalResult = await _engine.EvalAsync(def.Body);

        var sb = new StringBuilder();
        sb.AppendLine($"Saved: {def.RelativePath}");
        if (evalResult.Success)
        {
            sb.AppendLine("Loaded into session: OK");
            if (evalResult.ReturnValue != null)
                sb.AppendLine($"Return value: {evalResult.ReturnValue}");
        }
        else
        {
            sb.AppendLine($"Load error: {evalResult.Error}");
            sb.AppendLine("(File saved but not active in session. Fix the error and redefine.)");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "vivarium_list", ReadOnly = true), Description(
        "List all Vivarium source files with their descriptions and last-modified timestamps.")]
    public string List(
        [Description("Optional glob-style filter on path (simple substring match)")]
        string? filter = null)
    {
        var files = _fileStore.ScanAll();

        if (!string.IsNullOrEmpty(filter))
        {
            files = files.Where(f =>
                f.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (files.Count == 0)
            return "No Vivarium files found.";

        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.RelativePath))
        {
            sb.Append($"  {f.RelativePath}");
            if (f.Description != null)
                sb.Append($"  — {f.Description}");
            sb.AppendLine($"  (modified {f.LastModifiedUtc:yyyy-MM-dd HH:mm} UTC)");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "vivarium_view", ReadOnly = true), Description(
        "View the full source code of a Vivarium file.")]
    public string View(
        [Description("Relative path, e.g. 'Utils/Math.cs'")]
        string path)
    {
        var def = _fileStore.Read(path);
        if (def == null)
            return $"File not found: {path}";

        var sb = new StringBuilder();
        sb.AppendLine($"# {def.RelativePath}");
        if (def.Description != null)
            sb.AppendLine($"Description: {def.Description}");
        if (def.Dependencies.Count > 0)
            sb.AppendLine($"Depends on: {string.Join(", ", def.Dependencies)}");
        sb.AppendLine($"Modified: {def.LastModifiedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine(def.Source);
        sb.AppendLine("```");
        return sb.ToString();
    }

    [McpServerTool(Name = "vivarium_delete", Destructive = true), Description(
        "Delete a Vivarium source file. Warns if other files depend on it. " +
        "Does not remove already-loaded symbols from the current session (use vivarium_reset for that).")]
    public string Delete(
        [Description("Relative path to delete, e.g. 'Utils/Math.cs'")]
        string path)
    {
        var dependents = _fileStore.FindDependents(path);
        var sb = new StringBuilder();

        if (dependents.Count > 0)
        {
            sb.AppendLine($"Warning: {dependents.Count} file(s) depend on this:");
            foreach (var d in dependents)
                sb.AppendLine($"  {d}");
        }

        if (_fileStore.Delete(path))
        {
            sb.AppendLine($"Deleted: {path}");
            sb.AppendLine("Note: Already-loaded symbols remain in the live session until reset.");
        }
        else
        {
            sb.AppendLine($"File not found: {path}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "vivarium_inspect", ReadOnly = true), Description(
        "List all live variables in the Vivarium scripting session with their types and values.")]
    public string Inspect(
        [Description("Optional filter on variable name or type")]
        string? filter = null)
    {
        var vars = _engine.GetVariables(filter);
        if (vars.Count == 0)
            return "No variables in session" + (filter != null ? $" matching '{filter}'" : "") + ".";

        var sb = new StringBuilder();
        foreach (var v in vars)
        {
            sb.AppendLine($"  {v.Name} : {v.Type} = {v.ValueShort}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "vivarium_inspect_var", ReadOnly = true), Description(
        "Deep-inspect a specific variable: type, full value, public members.")]
    public string InspectVar(
        [Description("Name of the variable to inspect")]
        string name)
    {
        var detail = _engine.InspectVariable(name);
        if (detail == null)
            return $"Variable not found: {name}";

        var sb = new StringBuilder();
        sb.AppendLine($"Name: {detail.Name}");
        sb.AppendLine($"Type: {detail.Type}");
        sb.AppendLine($"Value: {detail.Value}");
        if (detail.Docstring != null)
            sb.AppendLine($"Signature: {detail.Docstring}");
        if (detail.Members != null && detail.Members.Count > 0)
        {
            sb.AppendLine($"Members ({detail.Members.Count}):");
            foreach (var m in detail.Members)
                sb.AppendLine($"  {m}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "vivarium_reset"), Description(
        "Reset the scripting session: clear all state, then reload all Vivarium files from disk. " +
        "Use this to recover from a bad session state.")]
    public async Task<string> Reset()
    {
        _engine.Reset();
        var result = await _loader.LoadAllAsync();
        return $"Session reset.\n{result}";
    }

    [McpServerTool(Name = "vivarium_search", ReadOnly = true), Description(
        "Search across all Vivarium file names and source code for a keyword.")]
    public string Search(
        [Description("Search query (substring match, case-insensitive)")]
        string query)
    {
        var hits = _fileStore.Search(query);
        if (hits.Count == 0)
            return $"No matches for '{query}'.";

        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            sb.AppendLine($"  {h.Path}: {h.MatchLine}");
        }
        return sb.ToString();
    }
}
