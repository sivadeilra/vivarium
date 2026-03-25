# Vivarium — Example Prompts and Workflows

Real examples of prompts a human can give an AI assistant that has Vivarium
installed, and how the AI might respond. Each example shows the human's
prompt, the AI's tool calls, and a brief explanation of why this approach
is better than the alternative.

---

## Example 1: Analyze a large log file

### Human prompt

> I have a build log at `C:\logs\build-2026-03-25.log` that's about 50,000
> lines. How many errors are there? What are the most common error codes?
> Which source files have the most errors?

### AI response

The AI loads the file once into memory and runs multiple queries against it,
rather than reading the file repeatedly:

```
vivarium_eval:
  var log = File.ReadAllLines(@"C:\logs\build-2026-03-25.log");
  log.Length

→ 48,291
```

```
vivarium_eval:
  var errors = log.Where(l => l.Contains(" error ")).ToList();
  errors.Count

→ 342
```

```
vivarium_eval:
  errors
    .Select(e => System.Text.RegularExpressions.Regex.Match(e, @"error (\w+\d+)").Groups[1].Value)
    .Where(code => code != "")
    .GroupBy(code => code)
    .OrderByDescending(g => g.Count())
    .Take(10)
    .Select(g => $"{g.Key}: {g.Count()}")

→ ["CS0246: 47", "CS1061: 31", "CS0103: 28", ...]
```

```
vivarium_eval:
  errors
    .Select(e => e.Split('(')[0].Trim())
    .GroupBy(f => f)
    .OrderByDescending(g => g.Count())
    .Take(10)
    .Select(g => $"{Path.GetFileName(g.Key)}: {g.Count()}")

→ ["NetworkManager.cs: 23", "AuthHandler.cs: 19", ...]
```

**Why this is better:** Without Vivarium, the AI would need to read the
50K-line file into its context window — possibly multiple times to answer
each sub-question. With Vivarium, the file is loaded into .NET memory once
(one tool call), and every subsequent query is a cheap eval against the
in-memory `log` and `errors` arrays.

---

## Example 2: Build a reusable CSV analysis tool

### Human prompt

> I frequently work with CSV exports from our bug tracker. Can you build me
> a tool that loads a CSV and lets me query it?

### AI response

The AI defines a persistent utility, then demonstrates it:

```
vivarium_define path="Utils/CsvTable.cs" source="""
//@VIVARIUM@
//@description: Load CSV files into queryable in-memory tables

using System.Text.RegularExpressions;

public class CsvTable
{
    public string[] Headers { get; }
    public List<string[]> Rows { get; }

    CsvTable(string[] headers, List<string[]> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    public static CsvTable Load(string path, char delimiter = ',')
    {
        var lines = File.ReadAllLines(path);
        var headers = lines[0].Split(delimiter);
        var rows = lines.Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split(delimiter))
            .ToList();
        return new CsvTable(headers, rows);
    }

    int Col(string name) =>
        Array.FindIndex(Headers, h =>
            h.Trim().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string[]> Where(string column, string value) =>
        Rows.Where(r => r[Col(column)].Trim()
            .Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    public IEnumerable<IGrouping<string, string[]>> GroupBy(string column) =>
        Rows.GroupBy(r => r[Col(column)].Trim());

    public string Cell(string[] row, string column) =>
        row[Col(column)].Trim();

    public void Summary() =>
        Console.WriteLine($"{Rows.Count} rows, {Headers.Length} columns: {string.Join(", ", Headers)}");
}
"""

→ Saved: Utils/CsvTable.cs
  Exports: CsvTable, CsvTable.Load(2), CsvTable.Where(2), ...
  Loaded into session: OK
```

Now the human can say things like:

> Load the latest bug export and tell me how many P1 bugs are open

```
vivarium_eval:
  var bugs = CsvTable.Load(@"C:\exports\bugs-march.csv");
  bugs.Summary()

→ 1,847 rows, 12 columns: ID, Title, Priority, Status, Assignee, ...
```

```
vivarium_eval:
  bugs.Where("Priority", "P1")
      .Where(r => bugs.Cell(r, "Status") != "Closed")
      .Count()

→ 34
```

**Why this is better:** `CsvTable` is now a permanent tool. It survives
server restarts (it's a file on disk). The next time the human asks about
any CSV, the AI can immediately call `CsvTable.Load()` — no need to
re-implement parsing.

---

## Example 3: Analyze a codebase with Roslyn

### Human prompt

> I want to understand the structure of the code in `C:\myproject\src`.
> How many types are there? Which ones have the most methods? Are there
> any really long methods I should refactor?

### AI response

The AI builds a code index using Roslyn (which is already loaded in
Vivarium's process):

```
vivarium_eval:
  #r "Microsoft.CodeAnalysis.dll"
  #r "Microsoft.CodeAnalysis.CSharp.dll"
```

```
vivarium_define path="Analysis/CodeIndex.cs" source="""
//@VIVARIUM@
//@description: Roslyn-based code structure index

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public class CodeIndex
{
    public record TypeInfo(string Name, string File, int Methods, int Properties, int Lines);
    public record MethodInfo(string Type, string Name, int Lines, int Params, bool IsAsync, string File);

    public List<TypeInfo> Types { get; init; }
    public List<MethodInfo> Methods { get; init; }

    public static CodeIndex Load(string directory)
    {
        var types = new List<TypeInfo>();
        var methods = new List<MethodInfo>();

        foreach (var file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            var root = tree.GetCompilationUnitRoot();

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var ms = type.Members.OfType<MethodDeclarationSyntax>().ToList();
                var ps = type.Members.OfType<PropertyDeclarationSyntax>().Count();
                var span = type.GetLocation().GetLineSpan();
                var lines = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;

                types.Add(new TypeInfo(type.Identifier.Text, Path.GetFileName(file), ms.Count, ps, lines));

                foreach (var m in ms)
                {
                    var mSpan = m.GetLocation().GetLineSpan();
                    var mLines = mSpan.EndLinePosition.Line - mSpan.StartLinePosition.Line + 1;
                    methods.Add(new MethodInfo(
                        type.Identifier.Text, m.Identifier.Text, mLines,
                        m.ParameterList.Parameters.Count,
                        m.Modifiers.Any(mod => mod.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AsyncKeyword)),
                        Path.GetFileName(file)));
                }
            }
        }
        return new CodeIndex { Types = types, Methods = methods };
    }
}
"""
```

```
vivarium_eval:
  var idx = CodeIndex.Load(@"C:\myproject\src");
  $"{idx.Types.Count} types, {idx.Methods.Count} methods"

→ "87 types, 412 methods"
```

```
vivarium_eval:
  idx.Types.OrderByDescending(t => t.Methods).Take(5)
      .Select(t => $"{t.Name}: {t.Methods} methods, {t.Lines} lines ({t.File})")

→ ["RequestHandler: 34 methods, 890 lines (RequestHandler.cs)", ...]
```

```
vivarium_eval:
  idx.Methods.Where(m => m.Lines > 50).OrderByDescending(m => m.Lines)
      .Select(m => $"{m.Type}.{m.Name}: {m.Lines} lines ({m.File})")

→ ["RequestHandler.ProcessBatch: 142 lines", "DataLoader.Transform: 97 lines", ...]
```

**Why this is better:** Parsing 87 source files with Roslyn takes a moment,
but it happens once. Every subsequent query — top types, long methods, async
methods, parameter counts — is an instant LINQ query against the live
`CodeIndex` object. Without Vivarium, each question would require re-reading
files into the context window.

---

## Example 4: Cross-reference two data sources

### Human prompt

> I have a list of expected API endpoints in `endpoints.json` and our actual
> route registrations are in `C:\api\src\Startup.cs`. Which endpoints are
> missing?

### AI response

```
vivarium_eval:
  using System.Text.Json;
  var expected = JsonSerializer.Deserialize<List<string>>(
      File.ReadAllText(@"C:\api\endpoints.json"));
  expected.Count

→ 45
```

```
vivarium_eval:
  var startup = File.ReadAllText(@"C:\api\src\Startup.cs");
  var registered = System.Text.RegularExpressions.Regex.Matches(startup, @"\.Map(Get|Post|Put|Delete)\(""([^""]+)""")
      .Select(m => m.Groups[2].Value)
      .ToHashSet();
  registered.Count

→ 41
```

```
vivarium_eval:
  var missing = expected.Where(e => !registered.Contains(e)).ToList();
  missing

→ ["/api/v2/users/export", "/api/v2/billing/invoices", "/api/v2/billing/refund",
   "/api/v2/admin/audit-log"]
```

**Why this is better:** Both data sources are loaded into memory. The
set-difference operation is instant. If the human wants to dig deeper ("which
of those are POST endpoints?"), the data is already there.

---

## Example 5: Session orientation with vivarium_catalog

### Human prompt

> I'm starting a new session. What Vivarium tools do I have available from
> last time?

### AI response

```
vivarium_catalog

→ Vivarium Library — 3 file(s)

  [Analysis]
    CodeIndex.cs — Roslyn-based code structure index
      exports:  CodeIndex, CodeIndex.Load(1)
      modified: 2026-03-25 14:30 UTC

  [Utils]
    CsvTable.cs — Load CSV files into queryable in-memory tables
      exports:  CsvTable, CsvTable.Load(2), CsvTable.Where(2), CsvTable.GroupBy(1)
      modified: 2026-03-25 13:15 UTC

    Forensics.cs — Session fingerprint utility
      exports:  Forensics, Forensics.Fingerprint()
      modified: 2026-03-24 22:00 UTC
```

The AI now knows exactly what's available without reading any source files.
It can immediately use `CsvTable.Load()` or `CodeIndex.Load()` in subsequent
evals.

---

## Example 6: Accumulating state across a multi-step investigation

### Human prompt

> Help me figure out why our test suite is slow. The test results are in
> `C:\results\test-run.xml` (JUnit format).

### AI response

```
vivarium_eval:
  using System.Xml.Linq;
  var doc = XDocument.Load(@"C:\results\test-run.xml");
  var tests = doc.Descendants("testcase")
      .Select(tc => new {
          Name = tc.Attribute("name")?.Value,
          Class = tc.Attribute("classname")?.Value,
          Time = double.Parse(tc.Attribute("time")?.Value ?? "0"),
          Failed = tc.Element("failure") != null
      }).ToList();
  $"{tests.Count} tests, {tests.Where(t => t.Failed).Count()} failures, total time: {tests.Sum(t => t.Time):F1}s"

→ "1,247 tests, 12 failures, total time: 847.3s"
```

> Which tests are the slowest?

```
vivarium_eval:
  tests.OrderByDescending(t => t.Time).Take(10)
      .Select(t => $"{t.Time:F1}s  {t.Class}.{t.Name}")

→ ["45.2s  IntegrationTests.DatabaseMigrationTest", "38.7s  IntegrationTests.FullSyncTest", ...]
```

> What percentage of total time is spent in the top 10 tests?

```
vivarium_eval:
  var top10Time = tests.OrderByDescending(t => t.Time).Take(10).Sum(t => t.Time);
  $"{top10Time:F1}s out of {tests.Sum(t => t.Time):F1}s ({top10Time / tests.Sum(t => t.Time) * 100:F1}%)"

→ "243.8s out of 847.3s (28.8%)"
```

> Group by test class — which class is slowest overall?

```
vivarium_eval:
  tests.GroupBy(t => t.Class)
      .OrderByDescending(g => g.Sum(t => t.Time))
      .Take(5)
      .Select(g => $"{g.Sum(t => t.Time):F1}s ({g.Count()} tests)  {g.Key}")

→ ["198.3s (23 tests)  IntegrationTests", "89.4s (45 tests)  ApiTests", ...]
```

**Why this is better:** The XML was parsed once into a typed anonymous-object
list. Five follow-up queries ran against the same in-memory data with zero
file re-reading. The human could continue asking questions indefinitely —
each answer is a sub-second eval call.

---

## Tips for writing good prompts

1. **Describe the data and the question.** Vivarium works best when you tell
   the AI where the data lives and what you want to know — let it choose
   whether to use `eval`, `define`, or both.

2. **Ask follow-up questions freely.** The whole point is that data stays in
   memory. "Now group those by X" or "filter to only the ones where Y" are
   exactly the prompts that get cheap, instant answers.

3. **Ask for reusable tools when you see a pattern.** "I keep loading CSVs —
   can you make a utility for that?" prompts the AI to use `define` instead
   of `eval`.

4. **Use `vivarium_catalog` at session start.** If you've built up tools in
   prior sessions, remind the AI to check what's already available.

5. **Don't over-specify the implementation.** The AI knows C# and LINQ well.
   "Analyze my build log" is a better prompt than "read the file line by line
   and use a regex to extract error codes" — let the AI choose the approach.
