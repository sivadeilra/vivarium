using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Vivarium;

// Resolve the .vivarium root directory.
// Priority: --root arg > VIVARIUM_ROOT env var > current directory
string? rootOverride = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--root")
    {
        rootOverride = args[i + 1];
        break;
    }
}

var vivariumRoot = rootOverride
    ?? Environment.GetEnvironmentVariable("VIVARIUM_ROOT")
    ?? Path.Combine(Directory.GetCurrentDirectory(), ".vivarium");

vivariumRoot = Path.GetFullPath(vivariumRoot);

var builder = Host.CreateApplicationBuilder(args);

// All logging goes to stderr (MCP uses stdout for protocol messages)
builder.Logging.AddConsole(opts =>
{
    opts.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register Vivarium services as singletons
builder.Services.AddSingleton(_ =>
{
    var store = new FileStore(vivariumRoot);
    store.EnsureDirectories();
    return store;
});
builder.Services.AddSingleton<ScriptingEngine>();
builder.Services.AddSingleton<BootstrapLoader>();

// Register MCP server with stdio transport
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "Vivarium",
            Version = "0.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Bootstrap: load all existing Vivarium files into the scripting session
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vivarium");
var loader = host.Services.GetRequiredService<BootstrapLoader>();

logger.LogInformation("Vivarium starting. Root: {Root}", vivariumRoot);

var bootstrapResult = await loader.LoadAllAsync();
if (bootstrapResult.Loaded.Count > 0)
    logger.LogInformation("Bootstrap: loaded {Count} file(s)", bootstrapResult.Loaded.Count);
if (bootstrapResult.Errors.Count > 0)
{
    foreach (var err in bootstrapResult.Errors)
        logger.LogWarning("Bootstrap error in {Path}: {Error}", err.Path, err.Error);
}

await host.RunAsync();
