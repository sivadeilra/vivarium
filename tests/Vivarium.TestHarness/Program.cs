using Vivarium;

/// <summary>
/// Test harness that exercises the Vivarium components directly (no MCP transport).
/// Simulates the tool calls an AI agent would make.
/// </summary>

var testRoot = Path.Combine(Path.GetTempPath(), "vivarium-test-" + Guid.NewGuid().ToString("N")[..8]);
Console.WriteLine($"Test root: {testRoot}");

var fileStore = new FileStore(testRoot);
fileStore.EnsureDirectories();
var engine = new ScriptingEngine();
var loader = new BootstrapLoader(fileStore, engine);
var tools = new VivariumTools(engine, fileStore, loader);

int passed = 0, failed = 0;

async Task Test(string name, Func<Task> action)
{
    try
    {
        await action();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  PASS: {name}");
        Console.ResetColor();
        passed++;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  FAIL: {name}");
        Console.WriteLine($"        {ex.Message}");
        Console.ResetColor();
        failed++;
    }
}

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception($"Assertion failed: {message}");
}

// ============================================================
Console.WriteLine("\n=== Test 1: Basic eval (expression) ===");
await Test("eval returns expression result", async () =>
{
    var result = await tools.Eval("1 + 1");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("2"), "Expected result to contain '2'");
});

// ============================================================
Console.WriteLine("\n=== Test 2: Eval with Console.WriteLine ===");
await Test("eval captures stdout", async () =>
{
    var result = await tools.Eval("Console.WriteLine(\"hello vivarium\");");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("hello vivarium"), "Expected stdout to contain 'hello vivarium'");
});

// ============================================================
Console.WriteLine("\n=== Test 3: Variable persistence across evals ===");
await Test("variables persist across eval calls", async () =>
{
    await tools.Eval("var x = 42;");
    var result = await tools.Eval("x * 2");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("84"), "Expected 84");
});

// ============================================================
Console.WriteLine("\n=== Test 4: Define a file + execute it ===");
await Test("define saves file and loads into session", async () =>
{
    var source = @"
public static class MathUtils
{
    public static int Square(int n) => n * n;
    public static int Cube(int n) => n * n * n;
}";
    var result = await tools.Define("Utils/Math.cs", source);
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("Saved: Utils/Math.cs"), "Expected save confirmation");
    Assert(result.Contains("Loaded into session: OK"), "Expected successful load");
});

// ============================================================
Console.WriteLine("\n=== Test 5: Use defined class in eval ===");
await Test("eval can use class from define", async () =>
{
    var result = await tools.Eval("MathUtils.Square(7)");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("49"), "Expected 49");
});

// ============================================================
Console.WriteLine("\n=== Test 6: List files ===");
await Test("list shows defined files", async () =>
{
    var result = tools.List();
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("Utils/Math.cs"), "Expected Math.cs in listing");
});

// ============================================================
Console.WriteLine("\n=== Test 7: View file ===");
await Test("view returns source code", async () =>
{
    var result = tools.View("Utils/Math.cs");
    Console.WriteLine($"    {result.Trim()[..80]}...");
    Assert(result.Contains("MathUtils"), "Expected MathUtils class in source");
    Assert(result.Contains("Square"), "Expected Square method in source");
});

// ============================================================
Console.WriteLine("\n=== Test 8: Search ===");
await Test("search finds code by keyword", async () =>
{
    var result = tools.Search("Cube");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("Utils/Math.cs"), "Expected Math.cs in search results");
});

// ============================================================
Console.WriteLine("\n=== Test 9: Inspect variables ===");
await Test("inspect shows session variables", async () =>
{
    await tools.Eval("var myList = new List<int> { 1, 2, 3 };");
    var result = tools.Inspect();
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("myList"), "Expected myList in variables");
});

// ============================================================
Console.WriteLine("\n=== Test 10: Inspect specific variable ===");
await Test("inspect_var shows variable details", async () =>
{
    var result = tools.InspectVar("myList");
    Console.WriteLine($"    {result.Trim()[..Math.Min(result.Trim().Length, 120)]}...");
    Assert(result.Contains("List"), "Expected List type");
});

// ============================================================
Console.WriteLine("\n=== Test 11: Syntax error handling ===");
await Test("eval handles syntax errors gracefully", async () =>
{
    var result = await tools.Eval("int x = ;");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("error"), "Expected error in result");
});

// ============================================================
Console.WriteLine("\n=== Test 12: Runtime exception handling ===");
await Test("eval handles runtime exceptions gracefully", async () =>
{
    var result = await tools.Eval("throw new InvalidOperationException(\"test error\");");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("test error"), "Expected exception message");
});

// ============================================================
Console.WriteLine("\n=== Test 13: Define second file with dependency ===");
await Test("define file with @depends metadata", async () =>
{
    var source = @"//@VIVARIUM@
//@description: Extended math utilities
//@depends: Math.cs

public static class ExtMath
{
    public static int SquarePlusOne(int n) => MathUtils.Square(n) + 1;
}";
    var result = await tools.Define("Utils/ExtMath.cs", source);
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("Loaded into session: OK"), "Expected successful load");

    var evalResult = await tools.Eval("ExtMath.SquarePlusOne(5)");
    Console.WriteLine($"    ExtMath.SquarePlusOne(5) = {evalResult.Trim()}");
    Assert(evalResult.Contains("26"), "Expected 26");
});

// ============================================================
Console.WriteLine("\n=== Test 14: Reset and re-bootstrap ===");
await Test("reset clears state and reloads from files", async () =>
{
    var result = await tools.Reset();
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("Session reset"), "Expected reset confirmation");

    // Verify reloaded classes work
    var evalResult = await tools.Eval("MathUtils.Cube(3)");
    Console.WriteLine($"    MathUtils.Cube(3) = {evalResult.Trim()}");
    Assert(evalResult.Contains("27"), "Expected 27");
});

// ============================================================
Console.WriteLine("\n=== Test 15: Delete with dependency warning ===");
await Test("delete warns about dependents", async () =>
{
    var result = tools.Delete("Utils/Math.cs");
    Console.WriteLine($"    {result.Trim()}");
    Assert(result.Contains("Warning"), "Expected dependency warning");
    Assert(result.Contains("ExtMath.cs"), "Expected ExtMath.cs mentioned");
    Assert(result.Contains("Deleted"), "Expected deletion confirmation");
});

// ============================================================
// Summary
Console.WriteLine($"\n{'='} Results: {passed} passed, {failed} failed {'='}");

// Cleanup
try { Directory.Delete(testRoot, recursive: true); } catch { }

return failed > 0 ? 1 : 0;
