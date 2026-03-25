# Vivarium — AI Usage Guide

You have access to **Vivarium**, a live C# scripting environment running as an
MCP server. This guide explains how to use it effectively — and more
importantly, *why* it changes the way you should approach problems.

## The core idea: stop re-reading, start accumulating

Without Vivarium, your typical workflow for data-heavy tasks is:

1. Read a file → parse it in your head → answer a question
2. Need another fact? Read another file. Parse again. Burn more tokens.
3. Need to cross-reference? Read both files again. More tokens.

Each cycle consumes your context window — the most expensive resource you have.

**Vivarium flips this.** You write code *once* that parses, indexes, or
transforms data, and the result stays alive in memory as a real .NET object.
You can query that object instantly, repeatedly, from different angles — all
without re-reading files or re-parsing anything.

```
vivarium_eval: var data = File.ReadAllLines(@"C:\logs\errors.txt");
vivarium_eval: data.Length                    // → 14,832
vivarium_eval: data.Where(l => l.Contains("FATAL")).Count()   // → 23
vivarium_eval: data.Where(l => l.Contains("FATAL")).Take(5)   // first 5
```

You read the file once. Every subsequent query is free.

## Mental model: RAM and disk

Vivarium has two layers:

- **Session (RAM):** Everything you `eval` or `define` accumulates in a live
  Roslyn scripting session. Variables, classes, functions — they all persist
  across calls. Lost on server restart.

- **Files (disk):** When you `define` a file, it's saved under
  `.vivarium/src/` and automatically reloaded on restart. Use `define`
  for anything you want to keep.

**Use `eval` for throwaway exploration. Use `define` for reusable tools.**

## When to use Vivarium

Use it when the alternative would burn context window on repetitive work:

- **Parsing structured data** — read a file once, build an in-memory index,
  query it many times
- **Cross-referencing** — load two datasets, join them, filter, aggregate
- **Building utilities** — define a helper class once, call it from every
  subsequent eval
- **Stateful analysis** — accumulate results across multiple steps without
  re-reading source data
- **Code analysis** — use Roslyn (already loaded) to parse and query C#
  source files programmatically
- **HTTP/API calls** — `HttpClient` is available; fetch data once, work
  with it in memory
- **Prototyping algorithms** — iterate on logic with instant feedback,
  no compile-deploy cycle

## When NOT to use Vivarium

- Simple questions that don't need computation
- One-shot file reads where you won't re-query the data
- Tasks where the standard file/terminal tools are sufficient

## Tool reference

| Tool | Use for |
|---|---|
| `vivarium_eval` | Run any C# code. Variables persist across calls. |
| `vivarium_define` | Save a `.cs` file and load it into the session. Use for reusable code. |
| `vivarium_catalog` | See all defined files with their exports. **Start here** at the beginning of a session. |
| `vivarium_list` | Quick list of all files with descriptions. |
| `vivarium_view` | Read the source of a specific file. |
| `vivarium_search` | Find files/code by keyword. |
| `vivarium_inspect` | See all live variables with types and short values. |
| `vivarium_inspect_var` | Deep-inspect one variable: full value, members, type info. |
| `vivarium_delete` | Remove a file (warns about dependents). |
| `vivarium_reset` | Clear session and reload all files from disk. |

## Workflow patterns

### Pattern 1: Parse once, query many

```
vivarium_eval: var log = File.ReadAllLines(@"C:\data\build.log");
vivarium_eval: log.Length
vivarium_eval: log.Where(l => l.Contains("error")).Count()
vivarium_eval: log.Where(l => l.Contains("error")).GroupBy(l => l.Split(':')[0]).Select(g => $"{g.Key}: {g.Count()}").ToList()
```

### Pattern 2: Define a reusable tool

```
vivarium_define path="Utils/CsvLoader.cs" source="""
//@VIVARIUM@
//@description: Generic CSV parser that returns List<Dictionary<string, string>>

public static class CsvLoader
{
    public static List<Dictionary<string, string>> Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var headers = lines[0].Split(',');
        return lines.Skip(1).Select(line =>
        {
            var values = line.Split(',');
            return headers.Zip(values, (h, v) => (h, v))
                .ToDictionary(x => x.h.Trim(), x => x.v.Trim());
        }).ToList();
    }
}
"""
```

Now use it everywhere:
```
vivarium_eval: var orders = CsvLoader.Load(@"C:\data\orders.csv");
vivarium_eval: orders.Count
vivarium_eval: orders.Where(o => o["status"] == "failed").Count()
```

### Pattern 3: Build up complexity incrementally

```
vivarium_eval: #r "path/to/Some.dll"
vivarium_define path="Analysis/CodeIndex.cs" source="..."   // depends on the #r
vivarium_eval: var idx = CodeIndex.Load(@"C:\myproject\src");
vivarium_eval: idx.Methods.Where(m => m.IsAsync).Count()
vivarium_eval: idx.Types.GroupBy(t => t.Namespace).Select(g => $"{g.Key}: {g.Count()}")
```

### Pattern 4: Session start orientation

At the start of a new session, if you've used Vivarium before in this
workspace, run:

```
vivarium_catalog
```

This shows you everything that's been defined — descriptions, exports,
and dependencies — so you know what's already available without reading
every file.

## File metadata

When you `define` a file, Vivarium adds metadata headers:

```csharp
//@VIVARIUM@                          // required marker (auto-added)
//@description: what this does        // you write this
//@depends: Other/File.cs             // you write this if needed
//@exports: MyClass, MyClass.Run(2)   // auto-generated from your code
```

- `@description` — write this! Future-you (or another AI) will thank you.
- `@depends` — declare if your file needs another file loaded first.
- `@exports` — don't write this; it's auto-extracted from public symbols.

## Tips

- **Variables persist.** After `eval`, any variable you defined is available
  in the next `eval`. No need to re-declare.
- **Types persist.** A class defined in one eval is available in all
  subsequent evals.
- **`using` persists.** `using System.Text.Json;` in one eval applies to all
  following evals.
- **`#r` for assemblies.** Need a DLL? `#r "path/to/assembly.dll"` loads it
  into the session.
- **Full BCL available.** System.Linq, System.IO, System.Net.Http,
  System.Text.Json, System.Text.RegularExpressions — all pre-loaded.
- **Async works.** `await HttpClient.GetStringAsync(url)` — Roslyn scripting
  supports top-level await.
- **Errors don't kill the session.** A failed eval returns an error message
  but the session continues with its previous state intact.
