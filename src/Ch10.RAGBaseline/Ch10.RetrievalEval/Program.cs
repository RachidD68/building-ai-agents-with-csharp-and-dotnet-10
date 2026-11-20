// ============================================================================
// Retrieval evaluation: measure retrieval on its own, before any model answers
// ----------------------------------------------------------------------------
// An annotated set (eval-set.json) names the handbook chunks that answer each
// question: one chunk, several chunks, or none ("not in the handbook"). The
// run embeds the FAQ agent's handbook, ranks every chunk for every question,
// and reports per threshold:
//   HitRate@K  share of answerable questions with at least one relevant chunk retrieved
//   Recall@K   average share of each answerable question's relevant chunks retrieved
//   and how many no-answer questions abstained (nothing at or above the threshold).
//
//   dotnet run                  Ollama nomic-embed-text over the handbook, K = 3
//   dotnet run -- --k 5         another K
//   dotnet run -- --self-test   fixed vectors, every number checkable by hand; RESULT n/n
// ============================================================================

using System.Globalization;
using System.Numerics.Tensors;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Ch10.FaqAgent;
using Ch10.RetrievalEval;

if (args.Contains("--self-test")) return SelfTest.Run();

int kAt = Array.IndexOf(args, "--k");
int k = kAt >= 0 && kAt + 1 < args.Length ? int.Parse(args[kAt + 1], CultureInfo.InvariantCulture) : 3;

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();
string endpoint = config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434";
string modelId = config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text";
using IEmbeddingGenerator<string, Embedding<float>> embeddings = new OllamaSharp.OllamaApiClient(new Uri(endpoint), modelId);

EvalQuestion[] set = JsonSerializer.Deserialize<EvalQuestion[]>(
    await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "eval-set.json")))!;
IReadOnlyList<FaqChunk> chunks = await FaqIngest.LoadAsync(Path.Combine(AppContext.BaseDirectory, "docs"));

// An annotation that names a chunk the corpus does not have is a broken set, not a miss.
string[] unknown = [.. set.SelectMany(q => q.Relevant).Distinct().Where(id => chunks.All(c => c.Id != id))];
if (unknown.Length > 0)
{
    Console.Error.WriteLine($"eval-set.json names chunks that do not exist: {string.Join(", ", unknown)}");
    return 2;
}

Console.WriteLine($"=== Retrieval evaluation: {modelId}, K = {k} ===");
Console.WriteLine($"{chunks.Count} chunks; {set.Length} questions: {set.Count(q => q.Relevant.Length == 1)} one-evidence, " +
                  $"{set.Count(q => q.Relevant.Length > 1)} multi-evidence, {set.Count(q => !q.Answerable)} no-answer\n");

await FaqIngest.CheckModelAsync(embeddings);
GeneratedEmbeddings<Embedding<float>> chunkVectors = await embeddings.GenerateAsync(chunks.Select(c => c.Content));
GeneratedEmbeddings<Embedding<float>> questionVectors = await embeddings.GenerateAsync(set.Select(q => q.Question));

Dictionary<string, IReadOnlyList<Ranked>> rankings = [];
for (int i = 0; i < set.Length; i++)
{
    ReadOnlyMemory<float> q = FaqChunk.CheckDimensions(questionVectors[i].Vector);
    rankings[set[i].Id] = [.. chunks
        .Select((c, j) => new Ranked(c.Id, TensorPrimitives.CosineSimilarity(q.Span, chunkVectors[j].Vector.Span)))
        .OrderByDescending(r => r.Score).ThenBy(r => r.ChunkId, StringComparer.Ordinal)];
}

EvalReport open = RetrievalMetrics.Evaluate(set, rankings, k, double.MinValue);
Console.WriteLine($"Per question, top {k} without a threshold:");
foreach (QuestionScore s in open.Questions)
{
    string top = string.Join(", ", rankings[s.Question.Id].Take(k).Select(r =>
        $"{r.ChunkId}{(s.Question.Relevant.Contains(r.ChunkId) ? "*" : "")} {r.Score:F2}"));
    string score = s.Question.Answerable ? $"hit {(s.Hit ? 1 : 0)} recall {s.Recall:F2}" : "no answer";
    Console.WriteLine($"  {s.Question.Id}  {score,-17} {top}");
}
Console.WriteLine("  (* = annotated as relevant)\n");

double[] thresholds = [.. Enumerable.Range(0, 9).Select(i => 0.40 + 0.05 * i)];
List<EvalReport> sweep = [.. thresholds.Select(t => RetrievalMetrics.Evaluate(set, rankings, k, t))];
Console.WriteLine($"threshold  HitRate@{k}  Recall@{k}  no-answer abstained  answerable abstained  correct");
foreach (EvalReport r in sweep)
{
    Console.WriteLine($"{r.Threshold,9:F2}  {r.HitRate,9:F2}  {r.Recall,8:F2}  {r.CorrectAbstentions,10}/{r.NoAnswerCount}" +
                      $"  {r.FalseAbstentions,19}/{r.AnswerableCount}  {r.Correct,5}/{set.Length}");
}

EvalReport chosen = RetrievalMetrics.Choose(sweep);
double[] tied = [.. sweep.Where(r => r.Correct == chosen.Correct).Select(r => r.Threshold)];
Console.WriteLine($"\nChosen threshold {chosen.Threshold:F2} ({chosen.Correct}/{set.Length} correct; tied range " +
                  $"{tied.Min():F2}-{tied.Max():F2}). It holds for this corpus, this model and this set only.");
return 0;
