// ============================================================================
// Exercise 01 — Time MCP Server
// MCP server exposing GetCurrentTime and ConvertTime tools.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "TimeServer",
            Version = "1.0.0"
        };
    })
    .WithToolsFromAssembly()
    .WithStdioServerTransport();

using IHost host = builder.Build();
await host.RunAsync();
