// ============================================================================
// Evaluating a RAG agent: grounding the Contoso FAQ agent
// ----------------------------------------------------------------------------
// This is the evaluation companion to the RAG chapter's FAQ agent. It rebuilds the
// exact same RAG pipeline (embed -> vector search -> inject context via the
// built-in TextSearchProvider) and then runs an evaluation suite over a small
// GOLDEN SET of HR questions whose answers live in the knowledge base.
//
// The opening anecdote of the chapter is the first golden-set entry: "How many
// PTO days do new employees get?" The policy says 15 (0-2 years of service).
// The agent once confabulated 20. The value check below turns that incident
// into a test that fails loudly whenever the stated value drifts off the source.
//
// Two layers, mirroring the rest of the chapter:
//   1. LocalEvaluator (always on) — expected_value, a per-question VALUE check:
//      the exact policy value from the docs appears as a whole token and is not
//      negated ("115 days" and "Not 15 days; ... 20" fail). It is lexical, not
//      a faithfulness or groundedness score. no_idk_leak checks that the
//      "I don't know" fallback never answers a question the KB can answer.
//   2. MEAI GroundednessEvaluator (gated) — an LLM judge scores support from
//      the retrieved context. It runs when LlmProvider:Judge:Enabled is true
//      AND LlmProvider:Judge:Model names a different Ollama model from the
//      agent's: a model grading its own answers is lenient, so the sample
//      refuses that pairing and says so.
//
//   dotnet run -- --self-test   the value check and the judge pairing rule, offline; RESULT n/n
//
// Exit code 0 when every local value check passes; 1 otherwise — so this
// drops into CI as the nightly evaluation gate the observability chapter points at. The
// groundedness score itself comes from the gated LLM judge in step 4.
// ============================================================================

using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CommunityToolkit.VectorData.InMemory;
using AgenticAI.Common.Extensions;
using Ch21.FaqEval;

if (args.Contains("--self-test")) return FaqEvalSelfTest.Run();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAzureOpenAIProvider();   // the azure arm, from AgenticAI.Common.Azure
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Grounding the Contoso FAQ agent ===");
Console.WriteLine();

// ── Step 1: rebuild the FAQ agent's RAG pipeline ───────────────────
string provider = config["LlmProvider:Provider"] ?? "ollama";
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = provider.ToLowerInvariant() switch
{
    "azure" => new AzureOpenAIClient(
            new Uri(config.GetRequiredValue("LlmProvider:AzureOpenAI:Endpoint")),
            new DefaultAzureCredential())
        .GetEmbeddingClient(config["LlmProvider:AzureOpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    "openai" => new OpenAI.OpenAIClient(config.GetRequiredValue("LlmProvider:OpenAI:ApiKey"))
        .GetEmbeddingClient(config["LlmProvider:OpenAI:EmbeddingModel"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator(),

    "ollama" => new OllamaSharp.OllamaApiClient(
        new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
        config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text"),

    _ => throw new InvalidOperationException(
        $"Unsupported provider '{provider}' for embeddings. Use azure, openai, or ollama.")
};

InMemoryCollection<string, FaqChunk> collection = new("faq");
await collection.EnsureCollectionExistsAsync(cts.Token);

string docsPath = Path.Combine(AppContext.BaseDirectory, "docs");
string[] mdFiles = Directory.GetFiles(docsPath, "*.md");

List<FaqChunk> allChunks = [];
foreach (string filePath in mdFiles)
{
    string fileName = Path.GetFileNameWithoutExtension(filePath);
    string fileContent = await File.ReadAllTextAsync(filePath, cts.Token);
    string topic = fileContent.Split('\n', 2)[0].TrimStart('#', ' ');

    string[] paragraphs = fileContent.Split(
        ["\r\n\r\n", "\n\n"],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    for (int i = 0; i < paragraphs.Length; i++)
    {
        if (paragraphs[i].Length < 30) continue;
        allChunks.Add(new FaqChunk
        {
            Id = $"{fileName}-{i}",
            Content = paragraphs[i],
            Source = fileName,
            Topic = topic
        });
    }
}

string[] texts = allChunks.Select(c => c.Content).ToArray();
GeneratedEmbeddings<Embedding<float>> embeddings =
    await embeddingGenerator.GenerateAsync(texts, cancellationToken: cts.Token);
for (int i = 0; i < allChunks.Count; i++)
{
    allChunks[i].Embedding = FaqChunk.CheckDimensions(embeddings[i].Vector);
    await collection.UpsertAsync(allChunks[i], cts.Token);
}

Console.WriteLine($"Indexed {allChunks.Count} chunks from {mdFiles.Length} Contoso policy documents.");
Console.WriteLine();

FaqSearchService faqSearch = new(embeddingGenerator, collection, topK: 5, scoreThreshold: 0.35f);

TextSearchProvider ragProvider = new(
    faqSearch.SearchAsync,
    new TextSearchProviderOptions
    {
        SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke,
        RecentMessageMemoryLimit = 6,
    });

ChatClientAgentOptions options = new()
{
    Name = "ContosoFaqAgent",
    Description = "Contoso company FAQ agent grounded in internal documents",
    AIContextProviders = [ragProvider],
    ChatOptions = new ChatOptions
    {
        Instructions = """
            You are the Contoso FAQ Agent. Answer employee questions about company
            policies, benefits, procedures, and culture.

            Rules:
            - Answer ONLY from the provided context.
            - If the context does not contain the answer, reply:
              "I don't have information about that in our knowledge base."
            - Be concise. Never invent policies or numbers.
            """,
        Temperature = 0.2f,
        MaxOutputTokens = 600,
    }
};

ChatClientAgent agent = new(chatClient, options);

// ── Step 2: the golden set ────────────────────────────────────────
// Each entry pairs a question the knowledge base can answer with the exact
// policy value that MUST appear in a grounded answer. The first entry is the
// PTO question from the chapter's opening incident. Golden questions are
// curated against the source — phrased the way the policy actually states the
// fact ("0-2 years", "5+ consecutive days") so a correctly grounded answer
// retrieves the right chunk. A drifted or confabulated answer still fails the
// check loudly, which is the whole point.
GoldenItem[] golden =
[
    new("How many PTO days do employees with 0-2 years of service get?", GroundTruth: "15"),
    new("How many PTO days do employees with 6+ years get?", GroundTruth: "25"),
    new("How many company holidays does Contoso observe?", GroundTruth: "11"),
    new("For absences of 5+ consecutive days, how many days ahead must I submit the request?", GroundTruth: "30"),
];

string[] queries = golden.Select(g => g.Question).ToArray();
string[] expectedOutputs = golden.Select(g => g.GroundTruth).ToArray();

// ── Step 3: the LocalEvaluator (value checks, always on) ──────────
// "expected_value" is the load-bearing check: the response must state the
// exact policy value for this question, not negated. Because EvaluateAsync aligns the
// expectedOutput array with the queries positionally, the (response,
// expectedOutput) overload sees the ground-truth value for each item.
LocalEvaluator local = new(
    FunctionEvaluator.Create("expected_value",
        (string response, string? expectedOutput) =>
            expectedOutput is not null && TextMatch.StatesValue(response, expectedOutput)),
    FunctionEvaluator.Create("no_idk_leak",
        (string response) =>
            !response.Contains("don't have information", StringComparison.OrdinalIgnoreCase)));

Console.WriteLine($"Evaluating {queries.Length} golden questions with the LocalEvaluator (2 value checks each)...");
Console.WriteLine();

AgentEvaluationResults localResults = await agent.EvaluateAsync(
    queries,
    local,
    expectedOutput: expectedOutputs,
    cancellationToken: cts.Token);

Console.WriteLine("--- LocalEvaluator (expected_value + no_idk_leak value checks) ---");
Console.WriteLine($"Provider: {localResults.ProviderName}");
Console.WriteLine($"Passed:   {localResults.Passed}/{localResults.Total}");
Console.WriteLine($"Failed:   {localResults.Failed}");
Console.WriteLine(localResults.AllPassed
    ? "Every answer states the expected policy value (a lexical check, not a groundedness score)."
    : "VALUE CHECK FAILURE — an answer drifted off the source value (see the chapter's PTO anecdote).");
Console.WriteLine();

// Per-question breakdown: Items holds one MEAI EvaluationResult per query,
// InputItems holds the matching EvalItem (with the query text). A question
// "fails" when any of its check metrics is interpreted as failed.
IReadOnlyList<EvaluationResult> items = localResults.Items ?? [];
IReadOnlyList<EvalItem> inputs = localResults.InputItems ?? [];
for (int i = 0; i < items.Count && i < inputs.Count; i++)
{
    bool failed = items[i].Metrics.Values.Any(m => m.Interpretation?.Failed == true);
    string mark = failed ? "FAIL" : "pass";
    Console.WriteLine($"  [{mark}] {inputs[i].Query}");
}

// ── Step 4: MEAI GroundednessEvaluator (LLM judge, gated) ─────────
// The MEAI Quality GroundednessEvaluator asks an LLM judge to score, on a 1-5
// scale, how well the answer is supported by the grounding context. Its score
// means something only after the judge is calibrated against human labels
// (Ch21.JudgeCalibration), and only when the judge is not the agent's model.
string? judgeSkip = JudgePairing.SkipReason(
    config.GetValue("LlmProvider:Judge:Enabled", false), provider,
    config["LlmProvider:Ollama:Model"] ?? "llama3.2", config["LlmProvider:Judge:Model"]);
if (judgeSkip is not null)
{
    Console.WriteLine();
    Console.WriteLine($"[Groundedness judge skipped] {judgeSkip}");
}
else
{
    Console.WriteLine();
    Console.WriteLine($"--- MEAI GroundednessEvaluator (judge {config["LlmProvider:Judge:Model"]}, 1-5) ---");

    using IChatClient judgeClient = new OllamaSharp.OllamaApiClient(
        new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"), config["LlmProvider:Judge:Model"]!);
    ChatConfiguration judgeConfig = new(judgeClient);
    GroundednessEvaluator groundedness = new();

    foreach (GoldenItem item in golden)
    {
        // Retrieve the same context the agent would see, then ask the judge how
        // well a candidate answer is supported by it.
        IEnumerable<TextSearchProvider.TextSearchResult> hits =
            await faqSearch.SearchAsync(item.Question, cts.Token);
        string groundingContext = string.Join("\n", hits.Select(h => h.Text));

        AgentResponse answer = await agent.RunAsync(item.Question, cancellationToken: cts.Token);

        EvaluationResult judged = await groundedness.EvaluateAsync(
            messages: [new ChatMessage(ChatRole.User, item.Question)],
            modelResponse: new ChatResponse(new ChatMessage(ChatRole.Assistant, answer.Text)),
            chatConfiguration: judgeConfig,
            additionalContext: [new GroundednessEvaluatorContext(groundingContext)],
            cancellationToken: cts.Token);

        NumericMetric metric = judged.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName);
        Console.WriteLine($"  Q: {item.Question}");
        Console.WriteLine($"     groundedness = {metric.Value?.ToString() ?? "n/a"}/5  ({metric.Interpretation?.Rating})");
    }
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

return localResults.Passed == localResults.Total ? 0 : 1;

// ──────────────────────────────────────────────────────────────────
internal readonly record struct GoldenItem(string Question, string GroundTruth);

// The expected value must appear as a whole token and not right after a
// negation: "15" matches "15 PTO days" but not "115 days", "15.5 days" or "Not 15 days".
// The negations are a fixed list (not, no, never, cannot, any "n't" contraction,
// rather than, instead of), read after curly apostrophes are straightened.
// This is still a lexical signal (a value check), not a faithfulness score.
internal static class TextMatch
{
    public static bool StatesValue(string response, string expected)
    {
        string text = response.Replace('’', '\'');
        string value = System.Text.RegularExpressions.Regex.Escape(expected);
        const string Token = @"(?![\w]|[.,]\d)";
        bool present = System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\w]|\d[.,])" + value + Token);
        bool negated = System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b(?:not|no|never|cannot|\w+n't|rather than|instead of)\b(?:\s+\S+){0,2}?\s+" + value + Token,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return present && !negated;
    }
}

// The judge runs only when it was asked for, on Ollama, with a model that is not the agent's.
// Models are compared by name before the tag, so llama3.2, llama3.2:latest and llama3.2:3b
// are one model. An alias made with `ollama cp` has another name and is not caught.
internal static class JudgePairing
{
    public static string? SkipReason(bool enabled, string provider, string agentModel, string? judgeModel)
    {
        static string Norm(string m) => m.Split(':')[0].Trim();
        if (!enabled) return "set LlmProvider:Judge:Enabled to true and LlmProvider:Judge:Model to another Ollama model.";
        if (!string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
            return $"this sample wires the judge for the ollama provider only (provider is {provider}).";
        if (string.IsNullOrWhiteSpace(judgeModel)) return "LlmProvider:Judge:Model is not set.";
        return string.Equals(Norm(agentModel), Norm(judgeModel), StringComparison.OrdinalIgnoreCase)
            ? $"judge model {judgeModel} is the agent's model; a model grading its own answers is lenient. Pick another judge."
            : null;
    }
}

internal static class FaqEvalSelfTest
{
    public static int Run()
    {
        Console.WriteLine("=== FAQ evaluation: self-test (value check and judge pairing, no model) ===");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }
        Check(System.Text.RegularExpressions.Regex.IsMatch("Not 15 days; employees get 20 days.", @"(?<![\w])15(?![\w])"),
            "DEFECT REPRODUCED: the whole-token check alone accepts \"Not 15 days; employees get 20 days\"");
        Check(TextMatch.StatesValue("New employees (0-2 years) receive 15 PTO days per year.", "15"), "expected_value: \"15 PTO days\" passes");
        Check(!TextMatch.StatesValue("Employees get 115 days of PTO.", "15"), "expected_value: \"115 days\" fails");
        Check(!TextMatch.StatesValue("Not 15 days; employees get 20 days.", "15"), "expected_value: \"Not 15 days; ... 20\" fails");
        Check(!TextMatch.StatesValue("New employees don't get 15 PTO days per year.", "15")
              && !TextMatch.StatesValue("New employees don’t get 15 PTO days per year.", "15"),
            "expected_value: \"don't get 15 PTO days\" fails, with a straight or a curly apostrophe");
        Check(!TextMatch.StatesValue("Employees get 15.5 days of PTO.", "15"), "expected_value: \"15.5 days\" fails");
        Check(TextMatch.StatesValue("Contoso observes 11 company holidays per year.", "11"), "expected_value: \"11 company holidays\" passes");
        Check(JudgePairing.SkipReason(true, "ollama", "llama3.2", "llama3.2:latest")?.Contains("agent's model", StringComparison.Ordinal) == true,
            "judge = agent model (llama3.2 / llama3.2:latest): skipped, with the reason");
        Check(JudgePairing.SkipReason(true, "ollama", "llama3.2", "llama3.2:3b") is not null,
            "judge = another tag of the agent model (llama3.2:3b): skipped");
        Check(JudgePairing.SkipReason(true, "ollama", "llama3.2", "phi4") is null, "judge phi4 for agent llama3.2: allowed");
        Check(JudgePairing.SkipReason(false, "ollama", "llama3.2", "phi4") is not null, "judge not enabled: skipped");
        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
