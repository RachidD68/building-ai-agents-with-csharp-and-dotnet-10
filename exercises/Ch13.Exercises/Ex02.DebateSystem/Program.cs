// ============================================================================
// Exercise 02 — Debate System (Intermediate)
// A Proponent and a Skeptic debate one topic for three rounds, twice:
//   A. hand-rolled: ONE shared List<ChatMessage>, appended turn by turn
//   B. built-in Group Chat: CreateGroupChatBuilderWith + RoundRobinGroupChatManager,
//      MaximumIterationCount = 6 (the turn cap)
// Accepted when B stops at the cap, both transcripts have the same structure
// (Proponent, Skeptic, ... six turns) and the token totals of both are printed.
// A Judge scores each transcript once ("Proponent: X/10, Skeptic: Y/10").
//
//   dotnet run                    the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     a scripted model, no network; RESULT n/n
// FULLY STANDALONE.
// ============================================================================

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

bool selfTest = args.Contains("--self-test");
IChatClient model;
if (selfTest)
{
    model = new ScriptedChatClient();
}
else
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    using IHost host = builder.Build();
    model = CreateChatClient(host.Services.GetRequiredService<IConfiguration>());
}

Console.WriteLine(selfTest ? "=== Exercise: Debate System (self-test) ===\n" : "=== Exercise: Debate System ===\n");

const string topic = "Should all new web services use microservices?";
const int Rounds = 3;
long tokens = 0;
IChatClient counted = new TokenCountingChatClient(model, n => Interlocked.Add(ref tokens, n));

ChatClientAgent Debater(string name, string stance) => new(counted, name: name, instructions: $"""
    You are "{name}" in a structured technical debate on: "{topic}"
    You argue {stance}. Read the debate so far and answer your opponent's latest
    argument in 3 or 4 sentences.
    """);
ChatClientAgent proponent = Debater("Proponent", "FOR the position");
ChatClientAgent skeptic = Debater("Skeptic", "AGAINST the position");
ChatClientAgent judge = new(model, name: "Judge", instructions: """
    You are an impartial debate judge. Score the whole debate for each debater on a 1-10 scale.
    Reply in EXACTLY this format: Proponent: X/10, Skeptic: Y/10. <one-sentence explanation>
    """);

ChatMessage opening = new(ChatRole.User, $"Debate topic: {topic} Proponent argues FOR, Skeptic AGAINST. Proponent opens.");

// ── A. Hand-rolled: you own the shared log ────────────────────────────────
List<ChatMessage> log = [opening];
for (int round = 1; round <= Rounds; round++)
{
    foreach (ChatClientAgent speaker in (ChatClientAgent[])[proponent, skeptic])
    {
        AgentResponse response = await speaker.RunAsync(log);
        log.Add(new ChatMessage(ChatRole.Assistant, response.Text) { AuthorName = speaker.Name });
    }
}
long handRolledTokens = Interlocked.Exchange(ref tokens, 0);
List<(string Speaker, string Text)> handRolled = [.. log.Skip(1).Select(m => (m.AuthorName ?? "?", m.Text))];

// ── B. Group Chat: the manager owns the log and the turn cap ──────────────
Workflow workflow = AgentWorkflowBuilder
    .CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents) { MaximumIterationCount = 2 * Rounds })
    .AddParticipants(proponent, skeptic)
    .Build();
List<ChatMessage> transcript;
await using (Run run = await InProcessExecution.Default.RunAsync(workflow, opening))
{
    transcript = run.OutgoingEvents.OfType<WorkflowOutputEvent>()
        .Select(e => e.Data).OfType<List<ChatMessage>>().LastOrDefault() ?? [];
}
long groupChatTokens = Interlocked.Exchange(ref tokens, 0);
List<(string Speaker, string Text)> groupChat = [.. transcript
    .Where(m => m.Role == ChatRole.Assistant && m.Text.Length > 0).Select(m => (m.AuthorName ?? "?", m.Text))];

foreach ((string title, List<(string Speaker, string Text)> turns) in
         (ValueTuple<string, List<(string, string)>>[])[("A. Hand-rolled log", handRolled), ("B. Group Chat", groupChat)])
{
    Console.WriteLine($"--- {title} ---");
    foreach ((string speaker, string text) in turns)
        Console.WriteLine($"  [{speaker}] {text.ReplaceLineEndings(" ")}");
    string verdict = (await judge.RunAsync(string.Join("\n", turns.Select(t => $"{t.Item1}: {t.Item2}")))).Text;
    Console.WriteLine($"  [Judge] {verdict.ReplaceLineEndings(" ")}" +
                      (TryParseScores(verdict, out int p, out int s) ? $"  (parsed {p} vs {s})" : "  (scores not parsed)"));
    Console.WriteLine();
}

string[] structureA = [.. handRolled.Select(t => t.Speaker)], structureB = [.. groupChat.Select(t => t.Speaker)];
Console.WriteLine("--- Report ---");
Console.WriteLine($"  structure A: {string.Join(" ", structureA)}");
Console.WriteLine($"  structure B: {string.Join(" ", structureB)}");
Console.WriteLine($"  debate tokens (input + output, {(selfTest ? "scripted estimate" : "reported by the provider")}): " +
                  $"hand-rolled {handRolledTokens}, group chat {groupChatTokens}");

if (!selfTest) return 0;

int passed = 0, total = 0;
void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }
Console.WriteLine("\n-- Self-test --");
Expect($"the group chat stops at the turn cap ({structureB.Length} turns)", structureB.Length == 2 * Rounds);
Expect("both transcripts have the same structure", structureA.SequenceEqual(structureB));
Expect("speakers alternate, Proponent first", structureA.Select((s, i) => s == (i % 2 == 0 ? "Proponent" : "Skeptic")).All(x => x));
Expect("token totals are measured for both versions", handRolledTokens > 0 && groupChatTokens > 0);
Expect("the score parser reads 'Proponent: 7/10, Skeptic: 6/10'", TryParseScores("Proponent: 7/10, Skeptic: 6/10.", out int a, out int b) && (a, b) == (7, 6));
Expect("the score parser refuses a verdict without scores", !TryParseScores("A close debate.", out _, out _));
Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

/// <summary>Extracts "Proponent: X/10" and "Skeptic: Y/10" from the verdict.</summary>
static bool TryParseScores(string verdict, out int proponentScore, out int skepticScore)
{
    proponentScore = skepticScore = 0;
    Match pro = Regex.Match(verdict, @"Proponent:\s*(\d{1,2})\s*/\s*10", RegexOptions.IgnoreCase);
    Match ske = Regex.Match(verdict, @"Skeptic:\s*(\d{1,2})\s*/\s*10", RegexOptions.IgnoreCase);
    if (!pro.Success || !ske.Success) return false;
    proponentScore = int.Parse(pro.Groups[1].Value, CultureInfo.InvariantCulture);
    skepticScore = int.Parse(ske.Groups[1].Value, CultureInfo.InvariantCulture);
    return true;
}

/// <summary>Creates an IChatClient from configuration.</summary>
static IChatClient CreateChatClient(IConfiguration configuration)
{
    string provider = configuration["LlmProvider:Provider"]?.ToLowerInvariant() ?? "ollama";
    return provider switch
    {
        "azure" => new AzureOpenAIClient(new Uri(configuration["LlmProvider:AzureOpenAI:Endpoint"] ?? throw new InvalidOperationException("Missing endpoint")), new DefaultAzureCredential())
            .GetChatClient(configuration["LlmProvider:AzureOpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "openai" => new OpenAI.OpenAIClient(configuration["LlmProvider:OpenAI:ApiKey"] ?? throw new InvalidOperationException("Missing key"))
            .GetChatClient(configuration["LlmProvider:OpenAI:Model"] ?? "gpt-5.6-luna").AsIChatClient(),
        "ollama" => new OllamaSharp.OllamaApiClient(new Uri(configuration["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), configuration["LlmProvider:Ollama:Model"] ?? "llama3.2"),
        _ => throw new InvalidOperationException($"Unknown provider: '{provider}'")
    };
}

/// <summary>Adds the input and output tokens the provider reports for each call.</summary>
sealed class TokenCountingChatClient(IChatClient inner, Action<long> add) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        add((response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            foreach (UsageContent usage in update.Contents.OfType<UsageContent>())
                add((usage.Details.InputTokenCount ?? 0) + (usage.Details.OutputTokenCount ?? 0));
            yield return update;
        }
    }
}

/// <summary>A scripted model: each debater says which turn it is; usage is characters / 4.</summary>
sealed class ScriptedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = [.. messages];
        string instructions = options?.Instructions ?? "";
        string reply = instructions.Contains("judge") ? "Proponent: 7/10, Skeptic: 6/10. Both were concrete."
            : $"{(instructions.Contains("\"Proponent\"") ? "For" : "Against")}, turn {list.Count}.";
        long input = list.Sum(m => (long)m.Text.Length) / 4;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            Usage = new() { InputTokenCount = input, OutputTokenCount = reply.Length / 4 },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
