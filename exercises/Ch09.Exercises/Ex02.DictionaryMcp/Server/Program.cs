// ============================================================================
// Exercise 02 — Dictionary MCP Server
// MCP server that reads a JSON dictionary file and exposes DefineWord and
// FindSynonyms tools.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "DictionaryServer",
            Version = "1.0.0"
        };
    })
    .WithToolsFromAssembly()
    .WithStdioServerTransport();

using IHost host = builder.Build();
await host.RunAsync();
