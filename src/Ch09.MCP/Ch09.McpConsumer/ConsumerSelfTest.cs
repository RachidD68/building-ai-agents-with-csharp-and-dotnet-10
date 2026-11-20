using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Ch09.McpConsumer;

/// <summary>
/// Offline check: dotnet run -- --self-test. The real Ch09.McpServer runs as a
/// child process over stdio (its built dll, so build the solution first); the
/// model is scripted, so the check proves wiring, never answer quality.
/// </summary>
internal static class ConsumerSelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== MCP consumer: self-test ===");

        // bin/<Configuration>/net10.0 of this project -> the same folder of Ch09.McpServer.
        string serverDll = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory.Replace("Ch09.McpConsumer", "Ch09.McpServer"), "Ch09.McpServer.dll"));
        if (!File.Exists(serverDll))
        {
            Console.WriteLine($"  FAIL  Ch09.McpServer is not built (looked for bin/.../Ch09.McpServer.dll); build the solution first.");
            Console.WriteLine("\nRESULT 0/1");
            return 1;
        }

        Console.WriteLine("\n-- stdio: the real server as a child process --");
        ConcurrentQueue<string> stderr = new();
        await using McpClient mcp = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [serverDll],
            Name = "ContosoProductServer",
            StandardErrorLines = stderr.Enqueue,
        }));
        IList<McpClientTool> tools = await mcp.ListToolsAsync();
        string names = string.Join(", ", tools.Select(t => t.Name).Order());
        Expect("ListToolsAsync over stdio finds both catalog tools", names == "InventoryCheck, ProductLookup", $"tools: {names}");
        Expect("the server's name arrives in the handshake", mcp.ServerInfo?.Name == "ContosoProductServer",
            $"server: {mcp.ServerInfo?.Name}");
        Expect("McpClientTool is an AIFunction: it can go straight into an agent's tools",
            tools.All(t => t is AIFunction));

        Console.WriteLine("\n-- agent + MCP tools, scripted model --");
        ScriptedToolCaller model = new(
            new FunctionCallContent("call-1", "InventoryCheck", new Dictionary<string, object?> { ["sku"] = "KB-ERG-001" }),
            new FunctionCallContent("call-2", "DeleteProduct", new Dictionary<string, object?> { ["productId"] = 1001 }));
        ChatClientAgent agent = CatalogAgent.Create(model, tools);

        AgentResponse answer = await agent.RunAsync("Check the inventory for SKU KB-ERG-001.", await agent.CreateSessionAsync());
        Expect("the model was offered exactly the discovered tools", model.OfferedTools.FirstOrDefault() == names,
            $"offered: {model.OfferedTools.FirstOrDefault()}");
        string? result = model.ToolResults.FirstOrDefault();
        Expect("the agent ran InventoryCheck on the MCP server and fed the result back",
            result is not null && result.Contains("Units Available: 150"), $"tool result: {OneLine(result)}");
        Expect("the final answer is built on that result", answer.Text.Contains("Units Available: 150"), $"answer: {OneLine(answer.Text)}");

        AgentResponse second = await agent.RunAsync("Delete product 1001.", await agent.CreateSessionAsync());
        string? refused = model.ToolResults.Skip(1).FirstOrDefault();
        Expect("a tool the server never offered (DeleteProduct) is not invoked; the model gets an error",
            refused is not null && refused.Contains("not found", StringComparison.OrdinalIgnoreCase),
            $"tool result: {OneLine(refused)}");

        Expect("the server logged to stderr, so stdout stayed protocol-only", !stderr.IsEmpty,
            $"stderr lines: {stderr.Count}; first: {OneLine(stderr.FirstOrDefault())}");

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string OneLine(string? s)
    {
        if (s is null) return "(none)";
        string flat = s.ReplaceLineEndings(" | ");
        return flat.Length <= 100 ? flat : flat[..100] + "...";
    }

    static void Expect(string name, bool ok, string? detail = null)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        if (detail is not null) Console.WriteLine($"        {detail}");
    }
}

/// <summary>
/// A scripted model: on each user turn it asks for the next scripted tool call;
/// when the tool result comes back it answers with that result. It records the
/// tools it was offered and the results it was sent.
/// </summary>
internal sealed class ScriptedToolCaller(params FunctionCallContent[] calls) : IChatClient
{
    private readonly Queue<FunctionCallContent> _calls = new(calls);
    public List<string> OfferedTools { get; } = [];
    public List<string> ToolResults { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        FunctionResultContent? result = messages.LastOrDefault()?.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        if (result is not null)
        {
            string text = result.Result?.ToString() ?? result.Exception?.Message ?? "";
            ToolResults.Add(text);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"From the catalog: {text}")));
        }

        OfferedTools.Add(string.Join(", ", (options?.Tools ?? []).Select(t => t.Name).Order()));
        FunctionCallContent call = _calls.Dequeue();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
