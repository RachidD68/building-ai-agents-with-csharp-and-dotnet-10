// ============================================================================
// Chapter 9 — The Model Context Protocol (Knowledge Base MCP Server)
// An MCP server that reads Markdown files from a docs/ folder and exposes
// a SearchKnowledgeBase tool. This bridges to Chapter 10 (RAG) by showing
// simple keyword-based document retrieval.
//
// Key concepts:
//   - MCP server reading from the file system
//   - Simple keyword-based search over Markdown documents
//   - Foundation for the semantic search shown in Chapter 10
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "ContosoKnowledgeBase",
            Version = "1.0.0"
        };
    })
    .WithToolsFromAssembly()
    .WithStdioServerTransport();

using IHost host = builder.Build();
await host.RunAsync();
