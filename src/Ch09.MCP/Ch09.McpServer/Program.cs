// ============================================================================
// Chapter 9 — The Model Context Protocol (MCP Server)
// A console app MCP server that exposes product catalog tools over stdio.
//
// Key concepts:
//   - [McpServerTool] attribute to expose methods as MCP tools
//   - [McpServerToolType] to mark tool container classes
//   - AddMcpServer + WithStdioServerTransport for hosting
//   - WithToolsFromAssembly for automatic tool discovery
//
// Run this server standalone, or have Ch09.McpConsumer launch it as a child
// process over stdio.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "ContosoProductServer",
            Version = "1.0.0"
        };
    })
    .WithToolsFromAssembly()
    .WithStdioServerTransport();

using IHost host = builder.Build();
await host.RunAsync();
