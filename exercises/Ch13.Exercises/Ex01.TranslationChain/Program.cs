// ============================================================================
// Exercise 01 — Translation Chain (Basic)
// Three agents in sequence (Translator FR -> EN, Formalizer, Summarizer) against
// one agent that does all three jobs in one prompt, on the same three memos.
// The report prints model calls and latency for each, and both outputs go
// through the same typed check (SummaryCheck): 1 to 3 sentences, at most 80
// words, and every key fact of the memo still present.
//
//   dotnet run                    the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     a scripted model, no network; RESULT n/n
// FULLY STANDALONE.
// ============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
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

Console.WriteLine(selfTest ? "=== Exercise: Translation Chain (self-test) ===\n" : "=== Exercise: Translation Chain ===\n");

int calls = 0;
IChatClient counted = new CountingChatClient(model, () => Interlocked.Increment(ref calls));

ChatClientAgent translator = new(counted,
    instructions: "You are a French-to-English translator. Return ONLY the English translation.", name: "Translator");
ChatClientAgent formalizer = new(counted,
    instructions: "Rewrite this English text in a formal business tone, keeping every fact. Return ONLY the text.", name: "Formalizer");
ChatClientAgent summarizer = new(counted,
    instructions: "Summarize the text in 2 or 3 sentences, keeping every date, number and name. Return ONLY the summary.", name: "Summarizer");
ChatClientAgent allInOne = new(counted,
    instructions: """
        Given a French text: translate it to English, rewrite it in a formal business tone, then
        summarize it in 2 or 3 sentences, keeping every date, number and name.
        Return ONLY the final English summary.
        """,
    name: "AllInOne");

(string Memo, string[] Facts)[] memos =
[
    ("""
     Objet : migration vers .NET 10. Salut l'équipe, on bascule les services de paiement sur
     .NET 10 lundi prochain. Mettez à jour vos pipelines de build et vérifiez vos packages NuGet.
     En cas de souci, postez sur le canal #migration.
     """, [".NET 10", "#migration"]),
    ("""
     Objet : notes de frais. Chaque note doit avoir un justificatif du même montant. Les nuits
     d'hôtel sont plafonnées à 150 EUR et les repas à 60 EUR par note.
     """, ["150", "60"]),
    ("""
     Objet : fermeture. Le bureau de Lyon sera fermé le 24 décembre. Les visites clients prévues
     ce jour-là passent au bureau de Paris.
     """, ["Lyon", "24", "Paris"]),
];

int chainCalls = 0, singleCalls = 0, chainPassed = 0, singlePassed = 0;
long chainMs = 0, singleMs = 0;
foreach ((string memo, string[] facts) in memos)
{
    calls = 0;
    Stopwatch sw = Stopwatch.StartNew();
    string english = await AskAsync(translator, memo);
    string formal = await AskAsync(formalizer, english);
    string chainSummary = await AskAsync(summarizer, formal);
    chainMs += sw.ElapsedMilliseconds;
    chainCalls += calls;

    calls = 0;
    sw.Restart();
    string singleSummary = await AskAsync(allInOne, memo);
    singleMs += sw.ElapsedMilliseconds;
    singleCalls += calls;

    string chainCheck = SummaryCheck.Check(chainSummary, facts), singleCheck = SummaryCheck.Check(singleSummary, facts);
    if (chainCheck == "ok") chainPassed++;
    if (singleCheck == "ok") singlePassed++;
    Console.WriteLine($"--- Memo: facts {string.Join(", ", facts)} ---");
    Console.WriteLine($"  chain  [{chainCheck}] {chainSummary.ReplaceLineEndings(" ")}");
    Console.WriteLine($"  single [{singleCheck}] {singleSummary.ReplaceLineEndings(" ")}\n");
}

Console.WriteLine("--- Report (same three memos, same typed check) ---");
Console.WriteLine($"  chain  : {chainPassed}/{memos.Length} pass, {chainCalls} calls, {(selfTest ? "-" : chainMs.ToString()),6} ms");
Console.WriteLine($"  single : {singlePassed}/{memos.Length} pass, {singleCalls} calls, {(selfTest ? "-" : singleMs.ToString()),6} ms");

if (!selfTest) return 0;

// The self-test checks the harness itself: call counts and the typed check.
int passed = 0, total = 0;
void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }
Console.WriteLine("\n-- Self-test --");
Expect($"the chain spends 3 calls per memo ({chainCalls} for {memos.Length})", chainCalls == 3 * memos.Length);
Expect($"the single prompt spends 1 call per memo ({singleCalls})", singleCalls == memos.Length);
Expect("both outputs pass the typed check on the scripted model", chainPassed == memos.Length && singlePassed == memos.Length);
Expect("a summary with every fact passes", SummaryCheck.Check("Payments move to .NET 10 on Monday. Ask in #migration.", [".NET 10", "#migration"]) == "ok");
Expect("a summary that drops a fact fails", SummaryCheck.Check("Payments move on Monday.", [".NET 10"]) == "missing .NET 10");
Expect("five sentences fail", SummaryCheck.Check("A 60. B. C. D. E.", ["60"]) == "5 sentences");
Expect("an empty summary fails", SummaryCheck.Check("  ", ["60"]) == "empty");
Console.WriteLine($"\nRESULT {passed}/{total}");
return passed == total ? 0 : 1;

async Task<string> AskAsync(ChatClientAgent agent, string input)
{
    AgentResponse response = await agent.RunAsync(input, await agent.CreateSessionAsync());
    return response.Text.Trim();
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

/// <summary>The typed check both outputs must pass.</summary>
static class SummaryCheck
{
    public static string Check(string summary, string[] facts)
    {
        if (string.IsNullOrWhiteSpace(summary)) return "empty";
        // A sentence ends at . ! or ? followed by a space or the end, so ".NET 10" is not a break.
        int sentences = System.Text.RegularExpressions.Regex.Split(summary.Trim(), @"[.!?](?:\s+|$)")
            .Count(s => s.Any(char.IsLetter));
        if (sentences is < 1 or > 3) return $"{sentences} sentences";
        int words = summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 80) return $"{words} words";
        string? missing = facts.FirstOrDefault(f => !summary.Contains(f, StringComparison.OrdinalIgnoreCase));
        return missing is null ? "ok" : $"missing {missing}";
    }
}

/// <summary>Counts every model call.</summary>
sealed class CountingChatClient(IChatClient inner, Action onCall) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        onCall();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        onCall();
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

/// <summary>
/// A scripted model for the self-test. Translator and Formalizer pass the text
/// through; Summarizer and AllInOne return one sentence holding the memo's facts.
/// </summary>
sealed class ScriptedChatClient : IChatClient
{
    static readonly string[][] Facts = [[".NET 10", "#migration"], ["150", "60"], ["Lyon", "24", "Paris"]];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = [.. messages];
        string system = options?.Instructions ?? "";   // ChatClientAgent sends its instructions here
        string input = list.Last(m => m.Role == ChatRole.User).Text;
        string reply = system.Contains("summarize", StringComparison.OrdinalIgnoreCase)
            ? "The memo covers " + string.Join(" and ", Facts.First(f => f.All(x => input.Contains(x)))) + "."
            : input;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
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
