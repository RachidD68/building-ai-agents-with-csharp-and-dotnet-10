// ============================================================================
// The Model Context Protocol (MCP Server)
// A console app MCP server that exposes product catalog tools over stdio.
//
// Key concepts:
//   - [McpServerTool] attribute to expose methods as MCP tools
//   - [McpServerToolType] to mark tool container classes
//   - AddMcpServer + WithStdioServerTransport for hosting
//   - WithToolsFromAssembly for automatic tool discovery
//   - stdout carries the protocol, so every log line goes to stderr
//
// Run this server standalone, or have Ch09.McpConsumer launch it as a child
// process over stdio.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Over stdio, stdout IS the JSON-RPC channel. The default console logger
// writes to stdout, so a single "Application started" line would land in the
// client's message stream. Send every level to stderr instead.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

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
