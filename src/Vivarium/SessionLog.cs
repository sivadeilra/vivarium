using System.Text;
using System.Text.RegularExpressions;

namespace Vivarium;

/// <summary>
/// Session-scoped log of all tool invocations and their outputs.
/// Provides truncation, pagination, and regex filtering.
/// </summary>
public sealed class SessionLog
{
    public const int DefaultMaxLines = 500;

    private readonly List<LogEntry> _entries = [];
    private int _nextId = 1;
    private StreamWriter? _diskLog;

    // Tracks files loaded via read_json that contained comments or trailing commas.
    // Keyed by normalized (full, case-insensitive) path.
    private readonly HashSet<string> _jsonTriviaFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record that a JSON file contained trivia (comments, trailing commas).</summary>
    public void MarkJsonTrivia(string fullPath) => _jsonTriviaFiles.Add(fullPath);

    /// <summary>Check whether a file was loaded with trivia that would be lost on rewrite.</summary>
    public bool HasJsonTrivia(string fullPath) => _jsonTriviaFiles.Contains(fullPath);

    /// <summary>
    /// Initialize disk logging to .vivarium/logs/viv-YYYY-MM-DD.log.
    /// Appends to existing file if one exists for today.
    /// </summary>
    public void InitDiskLog(string vivariumRoot)
    {
        try
        {
            var logsDir = Path.Combine(vivariumRoot, "logs");
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, $"viv-{DateTime.UtcNow:yyyy-MM-dd}.log");
            _diskLog = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
            _diskLog.WriteLine($"\n{new string('═', 72)}");
            _diskLog.WriteLine($"Session started at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            _diskLog.WriteLine(new string('═', 72));
        }
        catch
        {
            // Non-fatal — disk logging is best-effort
            _diskLog = null;
        }
    }

    /// <summary>
    /// Record a tool invocation and its full output. Returns the (possibly truncated) output
    /// along with the log entry ID header.
    /// </summary>
    public string Record(string toolName, string input, string fullOutput, int maxLines = DefaultMaxLines)
    {
        var entry = new LogEntry
        {
            Id = _nextId++,
            ToolName = toolName,
            Input = input,
            FullOutput = fullOutput,
            Timestamp = DateTime.UtcNow
        };

        lock (_entries)
            _entries.Add(entry);

        WriteToDisk(entry);

        return FormatOutput(entry, maxLines);
    }

    /// <summary>
    /// Read a specific log entry with optional line range and regex filter.
    /// </summary>
    public string Read(int id, int? startLine = null, int? endLine = null,
        string? pattern = null, int maxLines = DefaultMaxLines)
    {
        LogEntry? entry;
        lock (_entries)
            entry = _entries.FirstOrDefault(e => e.Id == id);

        if (entry == null)
            return $"Log entry #{id} not found.";

        var lines = entry.OutputLines;
        var totalLines = lines.Length;

        // Apply line range first
        int rangeStart = (startLine ?? 1) - 1; // convert to 0-based
        int rangeEnd = (endLine ?? totalLines) - 1;
        rangeStart = Math.Clamp(rangeStart, 0, totalLines - 1);
        rangeEnd = Math.Clamp(rangeEnd, rangeStart, totalLines - 1);

        IEnumerable<(int LineNum, string Text)> filtered = Enumerable
            .Range(rangeStart, rangeEnd - rangeStart + 1)
            .Select(i => (LineNum: i + 1, Text: lines[i]));

        // Apply regex filter
        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
                filtered = filtered.Where(x => regex.IsMatch(x.Text));
            }
            catch (RegexParseException ex)
            {
                return $"Invalid regex pattern: {ex.Message}";
            }
        }

        var matches = filtered.ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"[log #{entry.Id}] {entry.ToolName} — {totalLines} total lines");

        if (!string.IsNullOrEmpty(pattern))
            sb.AppendLine($"[filter: /{pattern}/i — {matches.Count} matching lines]");
        if (startLine != null || endLine != null)
            sb.AppendLine($"[range: lines {rangeStart + 1}-{rangeEnd + 1} of {totalLines}]");

        sb.AppendLine();

        var truncated = false;
        var shown = 0;
        foreach (var (lineNum, text) in matches)
        {
            if (shown >= maxLines)
            {
                truncated = true;
                break;
            }
            sb.AppendLine($"{lineNum,6}: {text}");
            shown++;
        }

        if (truncated)
        {
            var remaining = matches.Count - shown;
            sb.AppendLine($"\n(output truncated at {maxLines} lines; {remaining} more matching lines available; use startLine/endLine or pattern to narrow)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// List recent log entries (summary view).
    /// </summary>
    public string ListRecent(int count = 20)
    {
        List<LogEntry> recent;
        lock (_entries)
            recent = _entries.TakeLast(count).Reverse().ToList();

        if (recent.Count == 0)
            return "No log entries yet.";

        var sb = new StringBuilder();
        int totalCount;
        lock (_entries)
            totalCount = _entries.Count;
        sb.AppendLine($"Session log — {totalCount} total entries (showing last {recent.Count})\n");

        foreach (var e in recent)
        {
            var inputPreview = TruncateOneLine(e.Input, 80);
            sb.AppendLine($"  #{e.Id,-4} {e.ToolName,-22} {e.OutputLines.Length,6} lines  {e.Timestamp:HH:mm:ss}  {inputPreview}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Clear the log (called on session reset).
    /// </summary>
    public void Clear()
    {
        lock (_entries)
            _entries.Clear();
        _jsonTriviaFiles.Clear();
        _diskLog?.WriteLine($"\n── session reset at {DateTime.UtcNow:HH:mm:ss} UTC ──\n");
    }

    private void WriteToDisk(LogEntry entry)
    {
        if (_diskLog == null) return;
        try
        {
            _diskLog.WriteLine($"\n┌─ #{entry.Id} [{entry.ToolName}] {entry.Timestamp:HH:mm:ss} UTC ─");
            _diskLog.WriteLine($"│ input: {entry.Input}");
            _diskLog.WriteLine($"├─ output ─");
            _diskLog.WriteLine(entry.FullOutput);
            _diskLog.WriteLine($"└─");
        }
        catch
        {
            // Non-fatal
        }
    }

    private static string FormatOutput(LogEntry entry, int maxLines)
    {
        var lines = entry.OutputLines;
        var sb = new StringBuilder();
        sb.AppendLine($"[log #{entry.Id}]");

        if (lines.Length <= maxLines)
        {
            sb.Append(entry.FullOutput);
        }
        else
        {
            for (int i = 0; i < maxLines; i++)
            {
                sb.AppendLine(lines[i]);
            }
            var remaining = lines.Length - maxLines;
            sb.AppendLine($"\n(output truncated at {maxLines} lines; {remaining} more lines available; use vivarium_log with id={entry.Id} to paginate or filter)");
        }

        return sb.ToString();
    }

    private static string TruncateOneLine(string s, int maxLen)
    {
        var firstLine = s.Split('\n')[0].Trim();
        if (firstLine.Length <= maxLen) return firstLine;
        return firstLine[..(maxLen - 3)] + "...";
    }
}

public sealed class LogEntry
{
    public int Id { get; set; }
    public string ToolName { get; set; } = "";
    public string Input { get; set; } = "";
    public string FullOutput { get; set; } = "";
    public DateTime Timestamp { get; set; }

    private string[]? _outputLines;
    public string[] OutputLines => _outputLines ??= FullOutput.Split('\n');
}
