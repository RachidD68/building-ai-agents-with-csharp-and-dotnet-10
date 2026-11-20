// ============================================================================
// The Model Context Protocol (Knowledge Base MCP Server)
// An MCP server over stdio that reads the Markdown files in docs/ and offers
// them two ways:
//   - a SearchKnowledgeBase TOOL (keyword match, ranked by terms matched),
//     which a model calls;
//   - a kb://docs/{name} RESOURCE template, which an application reads by URI.
// The RAG chapter keeps the tool and swaps keyword matching for embeddings.
//
//   dotnet run                    serve over stdio
//   dotnet run -- --self-test     offline: matcher rules and the document lookup
// ============================================================================

using Ch09.KnowledgeBaseMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--self-test")) return KnowledgeBaseSelfTest.Run();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);   // stdout is the protocol

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
    .WithResourcesFromAssembly()
    .WithStdioServerTransport();

using IHost host = builder.Build();
await host.RunAsync();
return 0;
