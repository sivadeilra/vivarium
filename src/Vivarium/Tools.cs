using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
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
        "Run C# code in a persistent live session. All variables, types, and usings survive across calls. " +
        "Use this to query data already in memory, transform objects, define types, or run any .NET code. " +
        "Prefer vivarium_read_* tools to load files (they're cheaper than writing File.ReadAll* in eval). " +
        "When you know multiple queries upfront, use vivarium_eval_batch instead to save round trips. " +
        "Large output is auto-truncated; use vivarium_log to page through it.")]
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

    [McpServerTool(Name = "vivarium_eval_batch"), Description(
        "Run multiple C# expressions/statements in one call, returning all results together. " +
        "Each expression executes sequentially — later expressions can use variables defined by earlier ones. " +
        "This saves one inference round-trip per expression compared to calling vivarium_eval repeatedly. " +
        "Use this when you already know the sequence of queries you want to run: " +
        "load data, filter it, aggregate it, format the answer — all in a single tool call. " +
        "If any expression fails, subsequent expressions still execute (errors are reported inline). " +
        "Example: [\"var lines = File.ReadAllLines(@\\\"C:\\\\log.txt\\\")\", " +
        "\"var errors = lines.Where(l => l.Contains(\\\"error\\\")).ToList()\", " +
        "\"errors.Count\", " +
        "\"errors.GroupBy(e => e.Split(':')[0]).Take(5).Select(g => $\\\"{g.Key}: {g.Count()}\\\")\"]")]
    public async Task<string> EvalBatch(
        [Description("Array of C# expressions/statements to execute sequentially. " +
            "Each sees the session state left by all prior expressions in this batch.")]
        string[] expressions,
        [Description("Timeout in milliseconds per expression (default 30000)")]
        int timeoutMs = 30000,
        [Description("Maximum total output lines before truncation (default 500)")]
        int maxLines = SessionLog.DefaultMaxLines)
    {
        var sb = new StringBuilder();
        var inputSummary = new StringBuilder();

        for (int i = 0; i < expressions.Length; i++)
        {
            var expr = expressions[i];
            var preview = expr.Length > 80 ? expr[..77] + "..." : expr;
            inputSummary.AppendLine($"  [{i + 1}] {preview}");

            sb.AppendLine($"── [{i + 1}/{expressions.Length}] ──");
            sb.AppendLine($"> {preview}");

            var result = await _engine.EvalAsync(expr, timeoutMs);

            if (result.Success)
            {
                if (!string.IsNullOrEmpty(result.Stdout))
                    sb.Append(result.Stdout);
                if (result.ReturnValue != null)
                    sb.AppendLine(result.ReturnValue);
                else if (string.IsNullOrEmpty(result.Stdout))
                    sb.AppendLine("(ok)");
            }
            else
            {
                sb.AppendLine($"ERROR: {result.Error}");
            }

            sb.AppendLine();
        }

        return _log.Record("eval_batch", inputSummary.ToString(), sb.ToString(), maxLines);
    }

    [McpServerTool(Name = "vivarium_define"), Description(
        "Save reusable C# code to a persistent file AND load it into the live session. " +
        "Use this for classes, utilities, and functions you'll call more than once. " +
        "Files persist across restarts. Public symbols are auto-extracted as @exports: metadata.")]
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
        "List all saved Vivarium files with descriptions and timestamps.")]
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
        "View the full source of a saved Vivarium file.")]
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
        "Delete a saved Vivarium file. Warns about dependents. " +
        "Already-loaded symbols stay in the session until vivarium_reset.")]
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
        "List all live variables in the session with their types and values. " +
        "Use to see what data is already in memory before loading more.")]
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
        "Deep-inspect one variable: full value, type details, and public members.")]
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
        "Wipe the live session and reload all saved files from disk. Clears the session log. " +
        "Use to recover from a broken session state.")]
    public async Task<string> Reset()
    {
        _engine.Reset();
        _log.Clear();
        var result = await _loader.LoadAllAsync();
        return $"Session reset. Log cleared.\n{result}";
    }

    [McpServerTool(Name = "vivarium_search", ReadOnly = true), Description(
        "Search Vivarium file names and source code by keyword.")]
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
        "Show all saved Vivarium files with their exports, dependencies, and descriptions. " +
        "Call this at session start to see what tools and utilities are already available.")]
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
        "Browse the session log of all tool invocations. Without an id, lists recent entries. " +
        "With an id, retrieves the full output with optional line-range and regex filtering. " +
        "Use this to page through truncated output instead of re-running expensive commands.")]
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

    // ── Data Import Tools ──────────────────────────────────────────────

    [McpServerTool(Name = "vivarium_read_file"), Description(
        "Load a text file into a session variable as a string. " +
        "The data goes into memory, NOT your context window — then query it with eval. " +
        "For a 50K-line log file, you see a 5-line summary; the full content is in the variable. " +
        "Much cheaper than reading files through eval or other tools.")]
    public async Task<string> ReadFile(
        [Description("Absolute path to the file to read")]
        string path,
        [Description("Variable name to store the file contents in (e.g. 'log', 'config')")]
        string variableName)
    {
        if (!File.Exists(path))
            return _log.Record("read_file", path, $"File not found: {path}");

        var content = await File.ReadAllTextAsync(path);
        var result = await _engine.InjectVariableAsync(variableName, content, "string");
        if (!result.Success)
            return _log.Record("read_file", path, $"Failed to inject variable: {result.Error}");

        var lines = content.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"Loaded {path} into `{variableName}` (string)");
        sb.AppendLine($"  {content.Length:N0} chars, {lines.Length:N0} lines");
        sb.AppendLine($"  Preview (first 5 lines):");
        foreach (var line in lines.Take(5))
            sb.AppendLine($"    {Truncate(line, 120)}");
        if (lines.Length > 5)
            sb.AppendLine($"    ... ({lines.Length - 5} more lines)");
        return _log.Record("read_file", path, sb.ToString());
    }

    [McpServerTool(Name = "vivarium_read_lines"), Description(
        "Load a text file as a string[] (one element per line). " +
        "Gives indexed access (`lines[1432]`) and LINQ (`lines.Where(...)`) immediately. " +
        "Best for log files and line-oriented data. Returns a summary, not the content.")]
    public async Task<string> ReadLines(
        [Description("Absolute path to the file to read")]
        string path,
        [Description("Variable name to store the lines array in (e.g. 'lines', 'log')")]
        string variableName)
    {
        if (!File.Exists(path))
            return _log.Record("read_lines", path, $"File not found: {path}");

        var lines = await File.ReadAllLinesAsync(path);
        var result = await _engine.InjectVariableAsync(variableName, lines, "string[]");
        if (!result.Success)
            return _log.Record("read_lines", path, $"Failed to inject variable: {result.Error}");

        var sb = new StringBuilder();
        sb.AppendLine($"Loaded {path} into `{variableName}` (string[], {lines.Length:N0} elements)");
        sb.AppendLine($"  Preview (first 5 lines):");
        for (int i = 0; i < Math.Min(5, lines.Length); i++)
            sb.AppendLine($"    [{i}] {Truncate(lines[i], 120)}");
        if (lines.Length > 5)
            sb.AppendLine($"    ... ({lines.Length - 5} more lines)");
        return _log.Record("read_lines", path, sb.ToString());
    }

    [McpServerTool(Name = "vivarium_read_json"), Description(
        "Load and parse a JSON file into a JsonNode session variable. " +
        "Navigate with `node[\"key\"]`, `node[0]`, `node.AsArray()`, `node.GetValue<int>()`. " +
        "Returns a structure summary (keys or array length), not the full JSON. " +
        "Best for config files, API responses, and any structured data.")]
    public async Task<string> ReadJson(
        [Description("Absolute path to the JSON file")]
        string path,
        [Description("Variable name to store the parsed JsonNode in (e.g. 'config', 'data')")]
        string variableName)
    {
        if (!File.Exists(path))
            return _log.Record("read_json", path, $"File not found: {path}");

        string raw;
        JsonNode? node;
        try
        {
            raw = await File.ReadAllTextAsync(path);
            node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            return _log.Record("read_json", path, $"JSON parse error: {ex.Message}");
        }

        if (node == null)
            return _log.Record("read_json", path, "Parsed to null.");

        var result = await _engine.InjectVariableAsync(variableName, node, "System.Text.Json.Nodes.JsonNode");
        if (!result.Success)
            return _log.Record("read_json", path, $"Failed to inject variable: {result.Error}");

        // Detect trivia (comments, trailing commas) that would be lost on round-trip
        bool hasTrivia = raw.Contains("//") || raw.Contains("/*") ||
            System.Text.RegularExpressions.Regex.IsMatch(raw, @",\s*[}\]]");
        if (hasTrivia)
            _log.MarkJsonTrivia(Path.GetFullPath(path));

        var sb = new StringBuilder();
        sb.AppendLine($"Loaded {path} into `{variableName}` (JsonNode)");
        sb.AppendLine($"  {raw.Length:N0} chars");
        if (hasTrivia)
            sb.AppendLine("  ⚠ File contains comments or trailing commas. These will be lost if you rewrite it with vivarium_write_json.");
        sb.Append("  Root: ");
        if (node is JsonObject obj)
        {
            sb.AppendLine($"object with {obj.Count} keys");
            foreach (var key in obj.Select(kv => kv.Key).Take(15))
                sb.AppendLine($"    .{key}");
            if (obj.Count > 15)
                sb.AppendLine($"    ... ({obj.Count - 15} more keys)");
        }
        else if (node is JsonArray arr)
        {
            sb.AppendLine($"array with {arr.Count} elements");
            if (arr.Count > 0)
                sb.AppendLine($"    [0] = {Truncate(arr[0]?.ToJsonString() ?? "null", 120)}");
        }
        else
        {
            sb.AppendLine($"value: {Truncate(node.ToJsonString(), 120)}");
        }
        return _log.Record("read_json", path, sb.ToString());
    }

    [McpServerTool(Name = "vivarium_read_csv"), Description(
        "Load a CSV file into a List<Dictionary<string, string>> session variable. " +
        "Each row is a dictionary keyed by column headers — query with " +
        "`data.Where(r => r[\"Status\"] == \"Failed\").Count()`. " +
        "Returns column names and row count, not the data itself.")]
    public async Task<string> ReadCsv(
        [Description("Absolute path to the CSV file")]
        string path,
        [Description("Variable name to store the parsed rows in (e.g. 'bugs', 'results')")]
        string variableName,
        [Description("Delimiter character (default ',')")]
        string delimiter = ",")
    {
        if (!File.Exists(path))
            return _log.Record("read_csv", path, $"File not found: {path}");

        var delim = delimiter.Length > 0 ? delimiter[0] : ',';
        var allLines = await File.ReadAllLinesAsync(path);
        if (allLines.Length == 0)
            return _log.Record("read_csv", path, "File is empty.");

        var headers = ParseCsvLine(allLines[0], delim);
        var rows = new List<Dictionary<string, string>>();
        for (int i = 1; i < allLines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(allLines[i])) continue;
            var values = ParseCsvLine(allLines[i], delim);
            var row = new Dictionary<string, string>();
            for (int j = 0; j < headers.Length; j++)
                row[headers[j]] = j < values.Length ? values[j] : "";
            rows.Add(row);
        }

        var result = await _engine.InjectVariableAsync(
            variableName, rows, "List<Dictionary<string, string>>");
        if (!result.Success)
            return _log.Record("read_csv", path, $"Failed to inject variable: {result.Error}");

        var sb = new StringBuilder();
        sb.AppendLine($"Loaded {path} into `{variableName}` (List<Dictionary<string, string>>)");
        sb.AppendLine($"  {rows.Count:N0} rows, {headers.Length} columns");
        sb.AppendLine($"  Columns: {string.Join(", ", headers)}");
        if (rows.Count > 0)
        {
            sb.AppendLine($"  Row 0: {Truncate(string.Join(", ", rows[0].Select(kv => $"{kv.Key}={kv.Value}")), 200)}");
        }
        return _log.Record("read_csv", path, sb.ToString());
    }

    [McpServerTool(Name = "vivarium_read_xml"), Description(
        "Load and parse an XML file into an XDocument session variable. " +
        "Query with LINQ to XML: `doc.Descendants(\"testcase\")`, `el.Attribute(\"name\")`. " +
        "Best for .csproj, JUnit results, NuGet configs, and build output. " +
        "Returns a structure summary, not the full XML.")]
    public async Task<string> ReadXml(
        [Description("Absolute path to the XML file")]
        string path,
        [Description("Variable name to store the parsed XDocument in (e.g. 'doc', 'proj')")]
        string variableName)
    {
        if (!File.Exists(path))
            return _log.Record("read_xml", path, $"File not found: {path}");

        XDocument doc;
        try
        {
            var content = await File.ReadAllTextAsync(path);
            doc = XDocument.Parse(content);
        }
        catch (System.Xml.XmlException ex)
        {
            return _log.Record("read_xml", path, $"XML parse error: {ex.Message}");
        }

        var result = await _engine.InjectVariableAsync(variableName, doc, "System.Xml.Linq.XDocument");
        if (!result.Success)
            return _log.Record("read_xml", path, $"Failed to inject variable: {result.Error}");

        var sb = new StringBuilder();
        sb.AppendLine($"Loaded {path} into `{variableName}` (XDocument)");
        var root = doc.Root;
        if (root != null)
        {
            sb.AppendLine($"  Root element: <{root.Name.LocalName}>");
            var children = root.Elements().GroupBy(e => e.Name.LocalName)
                .Select(g => $"{g.Key} ({g.Count()})").Take(10);
            sb.AppendLine($"  Children: {string.Join(", ", children)}");
            var totalElements = doc.Descendants().Count();
            sb.AppendLine($"  Total elements: {totalElements:N0}");
        }
        return _log.Record("read_xml", path, sb.ToString());
    }

    [McpServerTool(Name = "vivarium_read_dir"), Description(
        "Scan a directory into a queryable List<FileEntry> session variable. " +
        "Each entry has Path, Name, Extension, Size, ModifiedUtc, IsDirectory. " +
        "Query with LINQ: `files.Where(f => f.Extension == \".cs\").Sum(f => f.Size)`. " +
        "Faster and cheaper than scripting Directory.GetFiles in eval.")]
    public async Task<string> ReadDir(
        [Description("Absolute path to the directory to scan")]
        string path,
        [Description("Variable name to store the file listing in (e.g. 'files', 'src')")]
        string variableName,
        [Description("Scan subdirectories recursively (default true)")]
        bool recursive = true)
    {
        if (!Directory.Exists(path))
            return _log.Record("read_dir", path, $"Directory not found: {path}");

        // First ensure the FileEntry type exists in the session
        await _engine.EvalAsync(@"
public record FileEntry(
    string Path, string Name, string Extension,
    long Size, DateTime ModifiedUtc, bool IsDirectory);
");

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = new List<object>(); // Will be cast to FileEntry in the session

        foreach (var file in new DirectoryInfo(path).EnumerateFileSystemInfos("*", option))
        {
            var isDir = file is DirectoryInfo;
            var size = isDir ? 0L : ((FileInfo)file).Length;
            entries.Add(new
            {
                Path = file.FullName,
                Name = file.Name,
                Extension = file.Extension,
                Size = size,
                ModifiedUtc = file.LastWriteTimeUtc,
                IsDirectory = isDir
            });
        }

        // Build entries through eval to use the in-session FileEntry type
        var key = DataBridge.Put(entries);
        var code = $@"
var __raw = DataBridge.Take<List<object>>(""{key}"");
var {variableName} = __raw.Select(o => {{
    dynamic d = o;
    return new FileEntry(
        (string)d.Path, (string)d.Name, (string)d.Extension,
        (long)d.Size, (DateTime)d.ModifiedUtc, (bool)d.IsDirectory);
}}).ToList();
{variableName}.Count";
        var result = await _engine.EvalAsync(code);
        if (!result.Success)
            return _log.Record("read_dir", path, $"Failed to inject variable: {result.Error}");

        var totalFiles = entries.Count(e => !((dynamic)e).IsDirectory);
        var totalDirs = entries.Count(e => ((dynamic)e).IsDirectory);
        var totalSize = entries.Where(e => !((dynamic)e).IsDirectory).Sum(e => (long)((dynamic)e).Size);

        var sb = new StringBuilder();
        sb.AppendLine($"Loaded directory listing into `{variableName}` (List<FileEntry>, {entries.Count:N0} entries)");
        sb.AppendLine($"  {totalFiles:N0} files, {totalDirs:N0} directories");
        sb.AppendLine($"  Total size: {FormatSize(totalSize)}");
        var topExts = entries.Where(e => !((dynamic)e).IsDirectory)
            .GroupBy(e => (string)((dynamic)e).Extension)
            .OrderByDescending(g => g.Count()).Take(5)
            .Select(g => $"{g.Key} ({g.Count()})");
        sb.AppendLine($"  Top extensions: {string.Join(", ", topExts)}");
        return _log.Record("read_dir", path, sb.ToString());
    }

    // ── Data Export Tools ──────────────────────────────────────────────

    [McpServerTool(Name = "vivarium_write_file"), Description(
        "Evaluate a C# expression and write the string result to a file. " +
        "Use for generating reports, saving transformed data, or exporting code.")]
    public async Task<string> WriteFile(
        [Description("Absolute path to write to")]
        string path,
        [Description("C# expression that evaluates to a string (e.g. a variable name like 'report', " +
            "or an expression like 'string.Join(\"\\n\", results)')")]
        string expression)
    {
        var (value, error) = await _engine.EvalObjectAsync(expression);
        if (error != null)
            return _log.Record("write_file", $"{path} ← {expression}", $"Expression error: {error}");

        var text = value?.ToString() ?? "";

        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, text);

        var lines = text.Split('\n').Length;
        return _log.Record("write_file", $"{path} ← {expression}",
            $"Wrote {text.Length:N0} chars ({lines:N0} lines) to {path}");
    }

    [McpServerTool(Name = "vivarium_write_json"), Description(
        "Serialize a session object to JSON and save to a file. " +
        "Supports compact or indented output. " +
        "Use for saving transformed data, configs, or API payloads.")]
    public async Task<string> WriteJson(
        [Description("Absolute path to write the JSON file to")]
        string path,
        [Description("C# expression that evaluates to the object to serialize (e.g. 'results', 'data.Where(...)' )")]
        string expression,
        [Description("Pretty-print with indentation (default true)")]
        bool indented = true)
    {
        // Warn if this file was loaded with trivia that will be lost
        var fullPath = Path.GetFullPath(path);
        bool triviaWarning = _log.HasJsonTrivia(fullPath);

        var (value, error) = await _engine.EvalObjectAsync(expression);
        if (error != null)
            return _log.Record("write_json", $"{path} ← {expression}", $"Expression error: {error}");

        var options = new JsonSerializerOptions { WriteIndented = indented };
        string json;
        try
        {
            json = JsonSerializer.Serialize(value, options);
        }
        catch (JsonException ex)
        {
            return _log.Record("write_json", $"{path} ← {expression}",
                $"Serialization error: {ex.Message}");
        }

        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, json);

        var msg = $"Wrote {json.Length:N0} chars to {path} ({(indented ? "indented" : "compact")})";
        if (triviaWarning)
            msg += "\n⚠ WARNING: This file previously contained comments or trailing commas that have been stripped. " +
                   "The original trivia is not recoverable from the parsed JsonNode.";

        return _log.Record("write_json", $"{path} ← {expression}", msg);
    }

    [McpServerTool(Name = "vivarium_write_csv"), Description(
        "Export a session collection to a CSV file. " +
        "Works with List<Dictionary<string,string>> or any IEnumerable of objects. " +
        "Use for sharing results with spreadsheets or other tools.")]
    public async Task<string> WriteCsv(
        [Description("Absolute path to write the CSV file to")]
        string path,
        [Description("C# expression that evaluates to an IEnumerable of objects to export " +
            "(e.g. 'results', 'bugs.Where(b => b[\"Priority\"] == \"P1\")')")]
        string expression,
        [Description("Delimiter character (default ',')")]
        string delimiter = ",")
    {
        var (value, error) = await _engine.EvalObjectAsync(expression);
        if (error != null)
            return _log.Record("write_csv", $"{path} ← {expression}", $"Expression error: {error}");

        if (value is not System.Collections.IEnumerable enumerable)
            return _log.Record("write_csv", $"{path} ← {expression}",
                "Expression must evaluate to an IEnumerable.");

        var delim = delimiter.Length > 0 ? delimiter[0] : ',';
        var sb = new StringBuilder();
        string[]? headers = null;
        int rowCount = 0;

        foreach (var item in enumerable)
        {
            if (item == null) continue;

            if (item is Dictionary<string, string> dict)
            {
                if (headers == null)
                {
                    headers = dict.Keys.ToArray();
                    sb.AppendLine(string.Join(delim, headers.Select(h => CsvEscape(h, delim))));
                }
                sb.AppendLine(string.Join(delim, headers.Select(h =>
                    CsvEscape(dict.TryGetValue(h, out var v) ? v : "", delim))));
            }
            else
            {
                // Use reflection for POCO objects
                var type = item.GetType();
                var props = type.GetProperties(System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);
                if (headers == null)
                {
                    headers = props.Select(p => p.Name).ToArray();
                    sb.AppendLine(string.Join(delim, headers.Select(h => CsvEscape(h, delim))));
                }
                sb.AppendLine(string.Join(delim, props.Select(p =>
                    CsvEscape(p.GetValue(item)?.ToString() ?? "", delim))));
            }
            rowCount++;
        }

        if (headers == null)
            return _log.Record("write_csv", $"{path} ← {expression}", "No data to export.");

        var csv = sb.ToString();
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, csv);

        return _log.Record("write_csv", $"{path} ← {expression}",
            $"Wrote {rowCount:N0} rows, {headers.Length} columns to {path}");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string Truncate(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        return s[..(maxLen - 3)] + "...";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    private static string[] ParseCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString().Trim());
        return fields.ToArray();
    }

    private static string CsvEscape(string value, char delimiter)
    {
        if (value.Contains(delimiter) || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
