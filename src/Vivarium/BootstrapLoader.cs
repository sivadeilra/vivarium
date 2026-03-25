namespace Vivarium;

/// <summary>
/// Orchestrates startup: scans files, sorts by dependencies, executes into engine.
/// </summary>
public sealed class BootstrapLoader
{
    private readonly FileStore _fileStore;
    private readonly ScriptingEngine _engine;

    public BootstrapLoader(FileStore fileStore, ScriptingEngine engine)
    {
        _fileStore = fileStore;
        _engine = engine;
    }

    /// <summary>
    /// Load all Vivarium files from disk into the scripting session.
    /// Returns results per file (success/failure).
    /// </summary>
    public async Task<BootstrapResult> LoadAllAsync()
    {
        var files = _fileStore.ScanAll();
        var sorted = TopologicalSort(files);

        var result = new BootstrapResult();

        foreach (var file in sorted)
        {
            var evalResult = await _engine.EvalAsync(file.Body);
            if (evalResult.Success)
            {
                result.Loaded.Add(file.RelativePath);
            }
            else
            {
                result.Errors.Add(new BootstrapError
                {
                    Path = file.RelativePath,
                    Error = evalResult.Error ?? "Unknown error"
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Topological sort by @depends metadata. Falls back to alphabetical for files
    /// without dependencies or in case of cycles.
    /// </summary>
    private static List<DefinitionFile> TopologicalSort(List<DefinitionFile> files)
    {
        // Build lookup by filename (not full path)
        var byName = new Dictionary<string, DefinitionFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var fileName = Path.GetFileName(f.RelativePath);
            byName.TryAdd(fileName, f);
        }

        var sorted = new List<DefinitionFile>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Sort input alphabetically for determinism
        var alphabetical = files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var file in alphabetical)
        {
            Visit(file, byName, visited, visiting, sorted);
        }

        return sorted;
    }

    private static void Visit(
        DefinitionFile file,
        Dictionary<string, DefinitionFile> byName,
        HashSet<string> visited,
        HashSet<string> visiting,
        List<DefinitionFile> sorted)
    {
        var key = file.RelativePath;
        if (visited.Contains(key)) return;
        if (visiting.Contains(key)) return; // cycle — skip to avoid infinite recursion

        visiting.Add(key);

        foreach (var dep in file.Dependencies)
        {
            if (byName.TryGetValue(dep, out var depFile))
            {
                Visit(depFile, byName, visited, visiting, sorted);
            }
        }

        visiting.Remove(key);
        visited.Add(key);
        sorted.Add(file);
    }
}

public class BootstrapResult
{
    public List<string> Loaded { get; set; } = [];
    public List<BootstrapError> Errors { get; set; } = [];

    public override string ToString()
    {
        var parts = new List<string>();
        if (Loaded.Count > 0)
            parts.Add($"Loaded {Loaded.Count} file(s): {string.Join(", ", Loaded)}");
        if (Errors.Count > 0)
            parts.Add($"Errors in {Errors.Count} file(s):\n" +
                string.Join("\n", Errors.Select(e => $"  {e.Path}: {e.Error}")));
        if (parts.Count == 0)
            parts.Add("No Vivarium files found.");
        return string.Join("\n", parts);
    }
}

public class BootstrapError
{
    public required string Path { get; set; }
    public required string Error { get; set; }
}
