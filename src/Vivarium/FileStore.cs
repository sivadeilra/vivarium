namespace Vivarium;

/// <summary>
/// Manages the .vivarium/project/ directory: scanning, reading, writing, and deleting
/// Vivarium source files identified by the //@VIVARIUM@ header marker.
/// </summary>
public sealed class FileStore
{
    public const string HeaderMarker = "//@VIVARIUM@";

    private readonly string _root;  // e.g. c:\workspace\.vivarium
    private string ProjectDir => Path.Combine(_root, "project");

    public FileStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    /// <summary>
    /// Ensure the .vivarium/project/ directory exists.
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ProjectDir);
    }

    /// <summary>
    /// Scan for all .cs files under project/ that have the //@VIVARIUM@ header.
    /// </summary>
    public List<DefinitionFile> ScanAll()
    {
        EnsureDirectories();
        var results = new List<DefinitionFile>();

        foreach (var fullPath in Directory.EnumerateFiles(ProjectDir, "*.cs", SearchOption.AllDirectories))
        {
            var def = TryParse(fullPath);
            if (def != null)
                results.Add(def);
        }

        return results;
    }

    /// <summary>
    /// Read and parse a single definition file by relative path (e.g. "Utils/Math.cs").
    /// </summary>
    public DefinitionFile? Read(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        if (!File.Exists(fullPath)) return null;
        return TryParse(fullPath);
    }

    /// <summary>
    /// Write a definition file. Auto-prepends //@VIVARIUM@ header if missing.
    /// Returns the parsed definition.
    /// </summary>
    public DefinitionFile Write(string relativePath, string source)
    {
        var fullPath = ResolvePath(relativePath);

        // Security: ensure resolved path is under ProjectDir
        var resolvedFull = Path.GetFullPath(fullPath);
        if (!resolvedFull.StartsWith(Path.GetFullPath(ProjectDir), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path escapes the project directory: {relativePath}");

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedFull)!);

        // Ensure header is present
        if (!source.TrimStart().StartsWith(HeaderMarker))
        {
            source = HeaderMarker + "\n" + source;
        }

        File.WriteAllText(resolvedFull, source);
        return TryParse(resolvedFull)!;
    }

    /// <summary>
    /// Delete a definition file. Returns true if the file existed.
    /// </summary>
    public bool Delete(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        var resolvedFull = Path.GetFullPath(fullPath);
        if (!resolvedFull.StartsWith(Path.GetFullPath(ProjectDir), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Path escapes the project directory: {relativePath}");

        if (!File.Exists(resolvedFull)) return false;
        File.Delete(resolvedFull);
        return true;
    }

    /// <summary>
    /// Search definitions by keyword in name or source content.
    /// </summary>
    public List<SearchHit> Search(string query)
    {
        var results = new List<SearchHit>();
        foreach (var def in ScanAll())
        {
            // Check name
            if (def.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new SearchHit { Path = def.RelativePath, MatchLine = $"(filename match)" });
                continue;
            }

            // Check source lines
            var lines = def.Source.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new SearchHit
                    {
                        Path = def.RelativePath,
                        MatchLine = $"L{i + 1}: {lines[i].Trim()}"
                    });
                    break; // one hit per file
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Find files that depend on the given file.
    /// </summary>
    public List<string> FindDependents(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return ScanAll()
            .Where(d => d.Dependencies.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(d => d.RelativePath)
            .ToList();
    }

    private string ResolvePath(string relativePath)
    {
        // Normalize separators
        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(ProjectDir, relativePath);
    }

    private DefinitionFile? TryParse(string fullPath)
    {
        string content;
        try
        {
            content = File.ReadAllText(fullPath);
        }
        catch
        {
            return null;
        }

        // Must have the header marker
        if (!content.TrimStart().StartsWith(HeaderMarker))
            return null;

        var relativePath = Path.GetRelativePath(ProjectDir, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var lastModified = File.GetLastWriteTimeUtc(fullPath);

        // Parse metadata from header comments
        string? description = null;
        var dependencies = new List<string>();
        var lines = content.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//@description:"))
                description = trimmed["//@description:".Length..].Trim();
            else if (trimmed.StartsWith("//@depends:"))
            {
                var deps = trimmed["//@depends:".Length..].Trim();
                dependencies.AddRange(
                    deps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (!trimmed.StartsWith("//@") && !string.IsNullOrWhiteSpace(trimmed))
                break; // stop parsing metadata after first non-metadata line
        }

        // Get the "body" — everything after the metadata header lines
        var body = GetBodyAfterHeader(content);

        return new DefinitionFile
        {
            RelativePath = relativePath,
            Source = content,
            Body = body,
            Description = description,
            Dependencies = dependencies,
            LastModifiedUtc = lastModified
        };
    }

    private static string GetBodyAfterHeader(string content)
    {
        var lines = content.Split('\n');
        int bodyStart = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("//@"))
            {
                bodyStart = i + 1;
                continue;
            }
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                bodyStart = i + 1;
                continue;
            }
            break;
        }
        return string.Join('\n', lines[bodyStart..]);
    }
}

public class DefinitionFile
{
    public required string RelativePath { get; set; }
    public required string Source { get; set; }
    public required string Body { get; set; }  // source without header metadata
    public string? Description { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public DateTime LastModifiedUtc { get; set; }
}

public class SearchHit
{
    public required string Path { get; set; }
    public required string MatchLine { get; set; }
}
