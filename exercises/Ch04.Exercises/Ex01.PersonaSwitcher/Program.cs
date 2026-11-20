// ============================================================================
// Exercise 01: Persona Switcher (Basic)
// ----------------------------------------------------------------------------
// Type a persona, then a question. Each question gets a fresh ChatClientAgent
// with that persona as its instructions; the IChatClient is shared, nothing
// else is. The persona text goes into the instructions verbatim, which is an
// injection vector: fine here, the security chapter treats it.
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     a fake IChatClient, no network; RESULT n/n
// ============================================================================

using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Persona Switcher ===");
Console.WriteLine("Describe a persona, then ask a question. An empty persona or 'quit' exits.\n");
while (!cts.IsCancellationRequested)
{
    Console.Write("Persona: ");
    string? persona = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(persona) || persona.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
    Console.Write("Question: ");
    string? question = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(question)) continue;

    try
    {
        AgentResponse reply = await Personas.For(chatClient, persona).RunAsync(question, cancellationToken: cts.Token);
        Console.WriteLine($"[{persona}]: {reply.Text.Trim()}\n");
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.WriteLine("[cancelled]");
    }
}
return 0;

static async Task<int> SelfTest()
{
    Console.WriteLine("=== Persona Switcher (self-test) ===");
    int passed = 0, total = 0;
    void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

    FakeChatClient model = new(FakeReply.Text("a"), FakeReply.Text("b"), FakeReply.Text("c"));
    ChatClientAgent pirate = Personas.For(model, "pirate");
    ChatClientAgent auditor = Personas.For(model, "strict auditor");
    await pirate.RunAsync("What is recursion?");
    await auditor.RunAsync("What is recursion?");
    await pirate.RunAsync("And iteration?");

    string?[] sent = [.. model.Calls.Select(c => c.Options?.Instructions)];
    Console.WriteLine($"   instructions sent: {string.Join(" | ", sent.Select(s => s?.Split('.')[0]))}");
    Expect("each call sends its own agent's persona", sent[0]!.Contains("pirate") && sent[1]!.Contains("strict auditor"));
    Expect("creating a second agent does not change the first", sent[2] == sent[0]);
    Expect("one shared client, two separate agents", model.Calls.Count == 3 && !ReferenceEquals(pirate, auditor));
    Expect("no history is shared: each call sends one user message",
        model.Calls.All(c => c.Messages.Count(m => m.Role == ChatRole.User) == 1));

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

internal static class Personas
{
    /// <summary>A fresh agent for one persona. The client is shared; the agent holds no state of its own.</summary>
    public static ChatClientAgent For(IChatClient chatClient, string persona) =>
        new(chatClient,
            instructions: $"You are a {persona}. Stay in that persona. Answer in at most three sentences.",
            name: "Persona");
}
