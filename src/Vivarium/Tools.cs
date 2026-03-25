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
    private readonly SessionLog _log;

    public VivariumTools(ScriptingEngine engine, FileStore fileStore, BootstrapLoader loader, SessionLog log)
    {
        _engine = engine;
        _fileStore = fileStore;
        _loader = loader;
        _log = log;
    }

    [McpServerTool(Name = "vivarium_eval"), Description(
        "Execute arbitrary C# code in the live Vivarium scripting session. " +
        "Variables, classes, and functions defined in prior eval calls persist. " +
        "Returns stdout, return value (if expression), and any errors. " +
        "Output is truncated if it exceeds maxLines; use vivarium_log to page through full results.")]
    public async Task<string> Eval(
        [Description("C# code to execute. Can be expressions, statements, class definitions, etc.")]
        string code,
        [Description("Timeout in milliseconds (default 30000)")]
        int timeoutMs = 30000,
        [Description("Maximum output lines before truncation (default 500)")]
        int maxLines = SessionLog.DefaultMaxLines)
    {
        var result = await _engine.EvalAsync(code, timeoutMs);
        return _log.Record("eval", code, result.ToString(), maxLines);
    }

    [McpServerTool(Name = "vivarium_define"), Description(
        "Create or update a Vivarium source file and execute it in the live session. " +
        "The file is persisted under .vivarium/project/ and survives restarts. " +
        "Use this to build up reusable functions, classes, and utilities. " +
        "Public symbols are automatically extracted and stored as @exports: metadata.")]
    public async Task<string> Define(
        [Description("Relative path under .vivarium/project/, e.g. 'Utils/Math.cs' or 'Helpers.cs'")]
        string path,
        [Description("Full C# source code. The //@VIVARIUM@ header is auto-added if missing. " +
            "Use //@description:, //@depends: OtherFile.cs metadata comments for organization.")]
        string source)
    {
        // Auto-extract exported public symbols and inject into file header
        var exports = SymbolExtractor.ExtractExports(source);
        var def = _fileStore.WriteWithExports(path, source, exports);
        var evalResult = await _engine.EvalAsync(def.Body);

        var sb = new StringBuilder();
        sb.AppendLine($"Saved: {def.RelativePath}");
        if (exports.Count > 0)
            sb.AppendLine($"Exports: {string.Join(", ", exports)}");
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
        return _log.Record("define", path, sb.ToString());
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
        return _log.Record("list", filter ?? "", sb.ToString());
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
        return _log.Record("view", path, sb.ToString());
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

        return _log.Record("delete", path, sb.ToString());
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
        return _log.Record("inspect", filter ?? "", sb.ToString());
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
        return _log.Record("inspect_var", name, sb.ToString());
    }

    [McpServerTool(Name = "vivarium_reset"), Description(
        "Reset the scripting session: clear all state and log, then reload all Vivarium files from disk. " +
        "Use this to recover from a bad session state.")]
    public async Task<string> Reset()
    {
        _engine.Reset();
        _log.Clear();
        var result = await _loader.LoadAllAsync();
        return $"Session reset. Log cleared.\n{result}";
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
        return _log.Record("search", query, sb.ToString());
    }

    [McpServerTool(Name = "vivarium_catalog", ReadOnly = true), Description(
        "Show a rich catalog of all Vivarium definitions: descriptions, exports, and dependencies. " +
        "Use this at the start of a session to understand what tools and utilities are available.")]
    public string Catalog(
        [Description("Optional filter on path (substring match)")]
        string? filter = null)
    {
        var files = _fileStore.ScanAll();

        if (!string.IsNullOrEmpty(filter))
            files = files.Where(f => f.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (files.Count == 0)
            return "No Vivarium files found.";

        // Group by directory
        var grouped = files
            .OrderBy(f => f.RelativePath)
            .GroupBy(f =>
            {
                var slash = f.RelativePath.LastIndexOf('/');
                return slash < 0 ? "(root)" : f.RelativePath[..slash];
            });

        var sb = new StringBuilder();
        sb.AppendLine($"Vivarium Library — {files.Count} file(s)\n");

        foreach (var group in grouped)
        {
            sb.AppendLine($"[{group.Key}]");
            foreach (var f in group)
            {
                var name = Path.GetFileName(f.RelativePath);
                sb.Append($"  {name}");
                if (f.Description != null)
                    sb.Append($" — {f.Description}");
                sb.AppendLine();

                if (f.Exports.Count > 0)
                    sb.AppendLine($"    exports:  {string.Join(", ", f.Exports)}");
                if (f.Dependencies.Count > 0)
                    sb.AppendLine($"    depends:  {string.Join(", ", f.Dependencies)}");
                sb.AppendLine($"    modified: {f.LastModifiedUtc:yyyy-MM-dd HH:mm} UTC");
            }
            sb.AppendLine();
        }
        return _log.Record("catalog", filter ?? "", sb.ToString());
    }

    [McpServerTool(Name = "vivarium_log", ReadOnly = true), Description(
        "Access the session log of all tool invocations. Without arguments, shows recent entries. " +
        "With an id, retrieves the full output of a specific invocation with optional line range " +
        "and regex filtering. Use this to page through truncated output or search within results.")]
    public string Log(
        [Description("Log entry ID to read. Omit to list recent entries.")]
        int? id = null,
        [Description("Start line (1-based, inclusive). Only used with id.")]
        int? startLine = null,
        [Description("End line (1-based, inclusive). Only used with id.")]
        int? endLine = null,
        [Description("Regex pattern to filter output lines (case-insensitive). Only used with id.")]
        string? pattern = null,
        [Description("Maximum lines to return (default 500)")]
        int maxLines = SessionLog.DefaultMaxLines)
    {
        if (id == null)
            return _log.ListRecent();

        return _log.Read(id.Value, startLine, endLine, pattern, maxLines);
    }
}
