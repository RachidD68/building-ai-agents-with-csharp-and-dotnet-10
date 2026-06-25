// ============================================================================
// Chapter 18 — Evaluating a RAG agent: grounding the Contoso FAQ agent
// ----------------------------------------------------------------------------
// This is the evaluation companion to Chapter 10's FAQ agent. It rebuilds the
// exact same RAG pipeline (embed -> vector search -> inject context via the
// built-in TextSearchProvider) and then runs an evaluation suite over a small
// GOLDEN SET of HR questions whose answers live in the knowledge base.
//
// The opening anecdote of the chapter is the first golden-set entry: "How many
// PTO days do new employees get?" The policy says 15 (0-2 years of service).
// The agent once confabulated 20. The grounding check below turns that incident
// into a test that fails loudly whenever the answer drifts off the source.
//
// Two layers, mirroring the rest of the chapter:
//   1. LocalEvaluator (always on, offline) — a per-question grounding check
//      that asserts the answer contains the exact policy value from the docs,
//      plus a global KeywordCheck that the "I don't know" fallback never leaks
//      into a question the KB can actually answer.
//   2. MEAI GroundednessEvaluator (gated) — an LLM-as-judge groundedness score
//      against the retrieved context. Runs when LlmProvider:Judge:Enabled is
//      true (it reuses the local Ollama model as the judge); skipped otherwise
//      with a printed note, so the suite stays runnable with zero cloud setup.
//
// Exit code 0 when every local grounding check passes; 1 otherwise — so this
// drops into CI as the "rag.groundedness" gate Chapter 14 points at.
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
using Microsoft.SemanticKernel.Connectors.InMemory;
using AgenticAI.Common.Extensions;
using Ch18.FaqEval;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 17: Grounding the Contoso FAQ agent ===");
Console.WriteLine();

// ── Step 1: rebuild the Chapter 10 RAG pipeline ───────────────────
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
    allChunks[i].Embedding = embeddings[i].Vector;
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

// ── Step 3: the LocalEvaluator (grounding checks, always on) ──────
// "grounded_answer" is the load-bearing check: the response must contain the
// exact policy value for this question. Because EvaluateAsync aligns the
// expectedOutput array with the queries positionally, the (response,
// expectedOutput) overload sees the ground-truth value for each item.
LocalEvaluator local = new(
    FunctionEvaluator.Create("grounded_answer",
        (string response, string? expectedOutput) =>
            expectedOutput is not null && response.Contains(expectedOutput, StringComparison.OrdinalIgnoreCase)),
    FunctionEvaluator.Create("no_idk_leak",
        (string response) =>
            !response.Contains("don't have information", StringComparison.OrdinalIgnoreCase)));

Console.WriteLine($"Evaluating {queries.Length} golden questions with the LocalEvaluator (2 grounding checks each)...");
Console.WriteLine();

AgentEvaluationResults localResults = await agent.EvaluateAsync(
    queries,
    local,
    expectedOutput: expectedOutputs,
    cancellationToken: cts.Token);

Console.WriteLine("--- LocalEvaluator (rag.groundedness gate) ---");
Console.WriteLine($"Provider: {localResults.ProviderName}");
Console.WriteLine($"Passed:   {localResults.Passed}/{localResults.Total}");
Console.WriteLine($"Failed:   {localResults.Failed}");
Console.WriteLine(localResults.AllPassed
    ? "All answers grounded in the policy docs."
    : "GROUNDING FAILURE — an answer drifted off the source (see the chapter's PTO anecdote).");
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
// scale, how well the answer is supported by the grounding context. It is the
// "did it stay on the source?" question expressed as a continuous score rather
// than a keyword assertion. We gate it because a judge model is real inference:
// when enabled it reuses the local Ollama model so the demo stays offline.
bool judgeEnabled = config.GetValue("LlmProvider:Judge:Enabled", false);
if (!judgeEnabled)
{
    Console.WriteLine();
    Console.WriteLine("[Groundedness judge skipped] Set LlmProvider:Judge:Enabled to true to run");
    Console.WriteLine("the MEAI GroundednessEvaluator (1-5 LLM-judged score) against the retrieved");
    Console.WriteLine("context. It reuses the local Ollama model as the judge — no cloud required.");
}
else
{
    Console.WriteLine();
    Console.WriteLine("--- MEAI GroundednessEvaluator (LLM judge, 1-5) ---");

    ChatConfiguration judgeConfig = new(chatClient);
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
