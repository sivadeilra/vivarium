# Vivarium — AI Orientation Guide

You are reading the source repository for **Vivarium**, an MCP server that
provides you with a persistent, incremental C# scripting environment. This
document tells you everything you need to know to understand, modify, and
extend this codebase.

## What this project IS

Vivarium is a **live programming workspace** exposed over the
[Model Context Protocol](https://modelcontextprotocol.io). It wraps the
Roslyn C# scripting API (`CSharpScript.RunAsync` / `ContinueWithAsync`) in
a persistent session and manages C# source files on disk as the "database."

When installed as an MCP server, it gives you 10 tools:

- `vivarium_eval` — run arbitrary C# in the live session
- `vivarium_define` — save + execute a persistent `.cs` file
- `vivarium_list` / `vivarium_view` / `vivarium_search` / `vivarium_catalog` — browse the library
- `vivarium_inspect` / `vivarium_inspect_var` — examine live session state
- `vivarium_delete` — remove a definition file
- `vivarium_reset` — wipe session state and reload everything from disk

## Key mental model

1. **Two layers of state:**
   - **Session state** (in-memory): Roslyn `ScriptState<object>` — all
     variables, type definitions, and usings from every `eval` or `define`
     call accumulate here. Lost on server restart. Think of it as RAM.
   - **File state** (on disk): `.vivarium/project/*.cs` files with
     `//@VIVARIUM@` headers. These are the persistent store. On startup,
     `BootstrapLoader` loads them in dependency order. Think of it as disk.

2. **Incremental execution:** Each `eval` or file load appends to a chain of
   `ScriptState` objects. Later code can reference anything defined earlier.
   This is the Roslyn "scripting" model, not the "compilation" model — there
   is no separate `Main()` entry point.

3. **File metadata convention:**
   ```
   //@VIVARIUM@                          ← required marker
   //@description: what this file does   ← optional, for humans and catalog
   //@depends: Other/File.cs             ← optional, controls load order
   //@exports: ClassName, Method(N)      ← auto-generated at define-time
   ```

## Source file map

All production code is in `src/Vivarium/` (6 files):

| File | Purpose | Key types |
|---|---|---|
| `Program.cs` | Server entry point. Configures DI, MCP stdio transport, resolves `.vivarium` root, triggers bootstrap. | — |
| `ScriptingEngine.cs` | Wraps Roslyn scripting. Maintains the `ScriptState` chain. Captures stdout/stderr. Returns `EvalResult`. | `ScriptingEngine`, `EvalResult`, `VariableInfo`, `VariableDetail` |
| `FileStore.cs` | Manages `.vivarium/project/` directory. File CRUD, metadata parsing, dependency search. | `FileStore`, `DefinitionFile`, `SearchHit` |
| `BootstrapLoader.cs` | On startup, reads all files from `FileStore`, topologically sorts by `@depends`, and executes them into the engine. | `BootstrapLoader` |
| `SymbolExtractor.cs` | At define-time, parses source with Roslyn and extracts public class/method names for `@exports:` metadata. | `SymbolExtractor` (static) |
| `Tools.cs` | All 10 MCP tool handlers. Each method is an `[McpServerTool]`. This is the MCP surface area. | `VivariumTools` |

Tests live in `tests/`:
- `Vivarium.TestHarness` — 15 unit tests exercising engine, file store, and bootstrap
- `Vivarium.McpTest` — end-to-end MCP protocol test over real stdio transport

## How to build

```
dotnet build src/Vivarium/Vivarium.csproj -c Release
```

Output: `src/Vivarium/bin/Release/net10.0/Vivarium.exe`

Target framework: .NET 10. Dependencies: `ModelContextProtocol 1.1.0`,
`Microsoft.CodeAnalysis.CSharp.Scripting 5.3.0`,
`Microsoft.Extensions.Hosting 10.0.5`.

## How to run tests

```
dotnet run --project tests/Vivarium.TestHarness       # unit tests
dotnet run --project tests/Vivarium.McpTest           # E2E MCP protocol test
```

## Architecture decisions you should know

1. **Why files, not a database?** Files are human-readable, diffable, and
   work with git. An AI can read them with standard file tools even when the
   MCP server isn't running. SQLite would add complexity without enough
   benefit for the typical scale (tens to low hundreds of files).

2. **Why Roslyn scripting, not compilation?** The scripting API
   (`CSharpScript`) supports incremental state accumulation — each eval
   builds on the last. The compilation API would require managing assemblies,
   entry points, and explicit references. Scripting is the right abstraction
   for a REPL-like environment.

3. **Why a single `ScriptState` chain?** This mirrors how a human uses a
   REPL: define something, use it, define more. The chain is append-only.
   `Reset()` nulls it and replays all files from disk.

4. **Why auto-exports?** As the library of defined files grows, an AI needs
   to quickly discover what's available without reading every file. The
   `@exports:` header + `vivarium_catalog` tool provide a table of contents.

5. **Why DI for tools?** The MCP SDK discovers tool methods via
   `[McpServerToolType]` + `[McpServerTool]` attributes and injects
   constructor dependencies. `VivariumTools` gets `ScriptingEngine`,
   `FileStore`, and `BootstrapLoader` via DI.

## If you are modifying this code

- **Adding a new tool:** Add a method to `Tools.cs` with `[McpServerTool]`
  and `[Description]` attributes. It's auto-discovered — no registration
  code needed.

- **Changing file metadata:** Update `FileStore.TryParse()` for reading and
  `FileStore.WriteCore()` for writing. The `DefinitionFile` model holds
  parsed metadata.

- **Changing default script references:** Edit the `ScriptOptions` setup in
  `ScriptingEngine.EvalAsync()`.

- **Changing bootstrap behavior:** Edit `BootstrapLoader.LoadAllAsync()`.
  It reads from `FileStore.ScanAll()` and topologically sorts.

- **Running the server manually:** `Vivarium.exe --root /path/to/.vivarium`
  or set `VIVARIUM_ROOT` env var.

## What this project is NOT

- Not a general-purpose .NET build system or compiler
- Not a sandboxed execution environment (code runs with full trust)
- Not a multi-user server (single session, single client)
- Not a replacement for proper project structure — it's a scratchpad and
  utility library builder for AI-assisted workflows

## License

Dual Apache 2.0 / MIT. See `LICENSE` file.
