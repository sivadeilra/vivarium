# Vivarium — Contributing / Internals Guide

This guide is for anyone — human or AI — who wants to understand, modify, or
extend the Vivarium codebase. For *using* Vivarium as a tool, see
[README_AI_USAGE.md](README_AI_USAGE.md) instead.

## Source file map

All production code is in `src/Vivarium/` (6 files):

| File | Purpose | Key types |
|---|---|---|
| `Program.cs` | Server entry point. Configures DI, MCP stdio transport, resolves `.vivarium` root, triggers bootstrap. | — |
| `ScriptingEngine.cs` | Wraps Roslyn scripting. Maintains the `ScriptState` chain. Captures stdout/stderr. Returns `EvalResult`. | `ScriptingEngine`, `EvalResult`, `VariableInfo`, `VariableDetail` |
| `FileStore.cs` | Manages `.vivarium/project/` directory. File CRUD, metadata parsing, dependency search. | `FileStore`, `DefinitionFile`, `SearchHit` |
| `BootstrapLoader.cs` | On startup, reads all files from `FileStore`, topologically sorts by `@depends`, and executes them into the engine. | `BootstrapLoader` |
| `SymbolExtractor.cs` | At define-time, parses source with Roslyn and extracts public class/method names for `@exports:` metadata. | `SymbolExtractor` (static) |
| `Tools.cs` | All MCP tool handlers. Each method is an `[McpServerTool]`. This is the MCP surface area. | `VivariumTools` |

Tests:

- `tests/Vivarium.TestHarness` — 15 unit tests (engine, file store, bootstrap)
- `tests/Vivarium.McpTest` — end-to-end MCP protocol test over real stdio

## Build and test

```
dotnet build src/Vivarium/Vivarium.csproj -c Release
dotnet run --project tests/Vivarium.TestHarness       # unit tests
dotnet run --project tests/Vivarium.McpTest           # E2E protocol test
```

Target framework: .NET 10. Dependencies:

- `ModelContextProtocol 1.1.0` — MCP SDK (attribute-based tool discovery)
- `Microsoft.CodeAnalysis.CSharp.Scripting 5.3.0` — Roslyn scripting engine
- `Microsoft.Extensions.Hosting 10.0.5` — host builder + DI

## How the pieces fit together

### Startup flow

1. `Program.cs` resolves the `.vivarium` root directory
   (`--root` arg → `VIVARIUM_ROOT` env → `$CWD/.vivarium`)
2. Registers `FileStore`, `ScriptingEngine`, `BootstrapLoader` as singletons
3. Configures MCP server with stdio transport and
   `WithToolsFromAssembly()` (discovers `[McpServerToolType]` classes)
4. On `ApplicationStarted`, calls `BootstrapLoader.LoadAllAsync()`

### Bootstrap flow

1. `FileStore.ScanAll()` finds all `.cs` files with `//@VIVARIUM@` headers
2. `BootstrapLoader` topologically sorts by `@depends:` metadata
3. Each file's body (everything after headers) is executed via
   `ScriptingEngine.EvalAsync()`
4. Failures are logged but don't abort — partial bootstrap is fine

### Eval flow

1. `ScriptingEngine.EvalAsync(code)` either calls `CSharpScript.RunAsync`
   (first eval) or `state.ContinueWithAsync` (subsequent evals)
2. Stdout/stderr are captured via `Console.SetOut` / `Console.SetError`
3. Returns `EvalResult` with success/error, stdout, return value, type, timing
4. The `ScriptState<object>` is stored for the next eval in the chain

### Define flow

1. `SymbolExtractor.ExtractExports(source)` parses with Roslyn and extracts
   public type/method names
2. `FileStore.WriteWithExports(path, source, exports)` saves the file with
   auto-injected `@exports:` header line
3. The file body is executed via `ScriptingEngine.EvalAsync(def.Body)`
4. Response includes the file path, extracted exports, and eval result

## Architecture decisions

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
   `Reset()` nulls the chain and replays all files from disk.

4. **Why auto-exports?** As the library grows, an AI needs to quickly
   discover what's available without reading every file. The `@exports:`
   header + `vivarium_catalog` tool provide a table of contents.

5. **Why DI for tools?** The MCP SDK discovers tool methods via
   `[McpServerToolType]` + `[McpServerTool]` attributes and injects
   constructor dependencies. `VivariumTools` receives `ScriptingEngine`,
   `FileStore`, and `BootstrapLoader` via constructor injection.

## How to make common changes

### Adding a new MCP tool

Add a method to `Tools.cs`:

```csharp
[McpServerTool(Name = "vivarium_mytool", ReadOnly = true), Description(
    "What this tool does.")]
public string MyTool(
    [Description("Parameter description")] string param)
{
    // Implementation
    return "result";
}
```

It's auto-discovered by `WithToolsFromAssembly()` — no registration needed.
Use `ReadOnly = true` for read-only tools, `Destructive = true` for
destructive ones (like `delete`).

### Adding new file metadata

1. Add a property to `DefinitionFile` (in `FileStore.cs`)
2. Parse it in `FileStore.TryParse()` — look for the `//@newfield:` pattern
3. Write it in `FileStore.WriteCore()` if it needs to be auto-injected
4. Surface it in `Tools.cs` (e.g., in `Catalog` or `View` output)

### Changing default script references

In `ScriptingEngine.EvalAsync()`, the `ScriptOptions` object specifies
default assembly references and `using` imports. Edit there to add or remove
defaults available to all eval sessions.

### Changing bootstrap behavior

`BootstrapLoader.LoadAllAsync()` controls startup loading. It calls
`FileStore.ScanAll()` and does a topological sort. Modify the sort, add
filtering, or change error handling here.

### Running the server manually

```
Vivarium.exe --root /path/to/.vivarium
```

Or set the `VIVARIUM_ROOT` environment variable. If neither is set, it uses
`$CWD/.vivarium`.

## Scope boundaries

Vivarium is intentionally narrow:

- **Not** a general-purpose build system or compiler
- **Not** a sandboxed environment (code runs with full trust in the host process)
- **Not** a multi-user server (single session, single MCP client)
- **Not** a replacement for proper project structure — it's a scratchpad
  and utility library builder for AI-assisted workflows

## Future direction (v2 ideas)

- **Dynamic tool registration:** AI writes `[McpServerTool]`-attributed
  methods → Vivarium compiles them in-memory → hot-loads them into the
  running MCP server's tool list via `notifications/tools/list_changed`.
  The AI creates new MCP tools within a conversation.
- **Assembly caching:** Compile frequently-used definitions into a cached
  assembly for faster bootstrap.
- **Workspace isolation:** Multiple independent scripting sessions scoped
  to different projects.

## License

Dual Apache 2.0 / MIT. See `LICENSE` file.
