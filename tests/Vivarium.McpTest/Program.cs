using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>
/// End-to-end MCP protocol test: starts Vivarium as a child process,
/// connects via stdio, and exercises the tools through the MCP protocol.
/// </summary>

var testRoot = Path.Combine(Path.GetTempPath(), "vivarium-e2e-" + Guid.NewGuid().ToString("N")[..8]);
Console.WriteLine($"E2E test root: {testRoot}");
Directory.CreateDirectory(testRoot);

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "Vivarium",
    Command = "dotnet",
    Arguments = ["run", "--project", @"C:\vivarium\src\Vivarium", "--", "--root", testRoot],
});

Console.WriteLine("Connecting to Vivarium MCP server...");
var client = await McpClient.CreateAsync(transport);
Console.WriteLine("Connected!");

// List tools
var tools = await client.ListToolsAsync();
Console.WriteLine($"\nRegistered tools ({tools.Count}):");
foreach (var tool in tools)
{
    Console.WriteLine($"  {tool.Name} — {tool.Description?[..Math.Min(tool.Description.Length, 60)]}...");
}

// Test eval
Console.WriteLine("\n--- eval: 2 + 2 ---");
var result = await client.CallToolAsync("vivarium_eval", new Dictionary<string, object?>
{
    ["code"] = "2 + 2"
});
Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);

// Test define
Console.WriteLine("\n--- define: Greeter.cs ---");
result = await client.CallToolAsync("vivarium_define", new Dictionary<string, object?>
{
    ["path"] = "Greeter.cs",
    ["source"] = "public static string Greet(string name) => $\"Hello, {name}!\";"
});
Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);

// Test eval using defined function
Console.WriteLine("\n--- eval: Greet(\"Vivarium\") ---");
result = await client.CallToolAsync("vivarium_eval", new Dictionary<string, object?>
{
    ["code"] = "Greet(\"Vivarium\")"
});
Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);

// Test list
Console.WriteLine("\n--- list ---");
result = await client.CallToolAsync("vivarium_list", new Dictionary<string, object?>());
Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);

// Test inspect
Console.WriteLine("\n--- eval + inspect ---");
await client.CallToolAsync("vivarium_eval", new Dictionary<string, object?>
{
    ["code"] = "var greeting = Greet(\"World\");"
});
result = await client.CallToolAsync("vivarium_inspect", new Dictionary<string, object?>());
Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);

// Test reset
Console.WriteLine("\n--- reset ---");
result = await client.CallToolAsync("vivarium_reset", new Dictionary<string, object?>());
Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);

// Test search
Console.WriteLine("\n--- search: Greet ---");
result = await client.CallToolAsync("vivarium_search", new Dictionary<string, object?>
{
    ["query"] = "Greet"
});
Console.WriteLine(result.Content.OfType<TextContentBlock>().First().Text);

Console.WriteLine("\n=== E2E test complete! All MCP protocol calls succeeded. ===");

await client.DisposeAsync();

// Cleanup
try { Directory.Delete(testRoot, recursive: true); } catch { }
