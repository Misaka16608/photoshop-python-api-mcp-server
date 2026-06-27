using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PhotoshopMcpServer.Services;

// =====================================================================
// Photoshop MCP Server — C# edition
// =====================================================================
// Architecture:
//   - PhotoshopBridge (singleton) — STA-thread COM isolation
//   - Tools (auto-discovered) — [McpServerToolType] + [McpServerTool]
//   - Resources (auto-discovered) — [McpServerResourceType] + [McpServerResource]
//
// To add new tools or resources:
//   1. Create a class with [McpServerToolType] or [McpServerResourceType]
//   2. Add methods with [McpServerTool] or [McpServerResource]
//   3. Inject PhotoshopBridge via constructor — done!
//
// No manual registration needed — WithToolsFromAssembly() and
// WithResourcesFromAssembly() scan for everything automatically.
// =====================================================================

var builder = Host.CreateApplicationBuilder(args);

// All logging goes to stderr so stdout stays clean for MCP JSON-RPC
builder.Logging.ClearProviders();
builder.Logging.AddConsole(consoleOptions =>
{
    consoleOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Register services
builder.Services.AddSingleton<PhotoshopBridge>();

// Build MCP server with stdio transport + auto-discovery
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "Photoshop",
            Version = "1.0.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

var host = builder.Build();

Console.Error.WriteLine("[photoshop-mcp-server] Starting MCP server...");

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[photoshop-mcp-server] Fatal error: {ex}");
    Environment.Exit(1);
}
