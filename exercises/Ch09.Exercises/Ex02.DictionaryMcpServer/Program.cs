// ============================================================================
// Exercise 02: Dictionary MCP Server
// Three tools (DefineWord, GetSynonyms, AddWord) over a small in-memory
// dictionary, served three ways:
//
//   dotnet run                    stdio (child process of the consumer)
//   dotnet run -- --http          streamable HTTP, no authentication,
//                                 http://localhost:5001/mcp  (intermediate)
//   dotnet run -- --protected     streamable HTTP as an OAuth resource server:
//                                 dictionary.read for the lookups,
//                                 dictionary.write for add_word  (challenge)
//                                 needs Dictionary:Issuer (your authorization server)
// ============================================================================

using Ex02.DictionaryMcpServer;

const string Url = "http://localhost:5001";

if (args.Contains("--http") || args.Contains("--protected"))
{
    bool protect = args.Contains("--protected");
    var web = WebApplication.CreateBuilder(args);
    if (protect)
    {
        string? issuer = web.Configuration["Dictionary:Issuer"];
        if (string.IsNullOrWhiteSpace(issuer))
        {
            Console.Error.WriteLine("Dictionary:Issuer is not set (the 'iss' of your authorization server's tokens).");
            Console.Error.WriteLine("Offline proof: dotnet run --project exercises/Ch09.Exercises/Ex02.DictionaryMcpConsumer -- --self-test");
            return 2;
        }
        web.Services.AddProtectedDictionaryMcp($"{Url}/mcp", issuer);
    }
    else
    {
        web.Services.AddOpenDictionaryMcp();
    }

    var app = web.Build();
    app.MapDictionaryMcp(requireToken: protect);
    await app.RunAsync(Url);
    return 0;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);   // stdout is the protocol

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "DictionaryServer",
            Version = "1.0.0"
        };
    })
    .WithTools([typeof(DictionaryTools)])
    .WithStdioServerTransport();

using IHost host = builder.Build();
await host.RunAsync();
return 0;
