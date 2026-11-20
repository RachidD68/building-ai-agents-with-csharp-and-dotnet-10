// ============================================================================
// Contoso v3: FAQ with evidence
// ----------------------------------------------------------------------------
// The expense assistant of v2 gains a handbook desk. An employee asks a policy
// question; the desk answers from retrieved handbook evidence or not at all:
//
//   authenticated caller -> tenant check -> retrieve (score threshold)
//     -> nothing relevant: abstain, the model is not called
//     -> model with the excerpts -> citations checked against the retrieval
//     -> Answered | withheld (Uncited, UnsupportedCitation) -> evidence record
//
// Alice asks whether ride-sharing is reimbursable before filing the taxi claim
// v2 pays; Bob asks about something the handbook does not cover; a caller of
// another tenant is refused. The self-test adds a model that answers without a
// citation, one that cites a source it was not given, one that declines, and
// six questions at the same moment on one desk.
//
//   dotnet run                    Ollama: nomic-embed-text + llama3.2
//   dotnet run -- --self-test     keyword embeddings + a scripted model; RESULT n/n
// ============================================================================

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AgenticAI.Common.Extensions;
using Ch08.ActionPolicy;
using Ch10.FaqAgent;
using CommunityToolkit.VectorData.InMemory;
using Contoso.AuthorizedReimbursement;
using Contoso.FaqEvidence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest ? "=== Contoso: FAQ with evidence (self-test) ===" : "=== Contoso: FAQ with evidence ===");

if (selfTest) return await SelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient model = host.Services.GetRequiredService<IChatClient>();
IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
using IEmbeddingGenerator<string, Embedding<float>> embeddings = new OllamaSharp.OllamaApiClient(
    new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
    config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text");

await FaqIngest.CheckModelAsync(embeddings);
IReadOnlyList<FaqChunk> chunks = await FaqIngest.LoadAsync(Path.Combine(AppContext.BaseDirectory, "docs"));
InMemoryCollection<string, FaqChunk> collection = new("handbook");
await FaqIngest.IngestAsync(embeddings, collection, chunks);
Console.WriteLine($"Handbook indexed: {chunks.Count} chunks.");

FaqDesk desk = new(model, new FaqSearchService(embeddings, collection, topK: 3, scoreThreshold: 0.55),
    Scenario.Directory(), Scenario.Tenant);
await Scenario.RunAsync(desk, assert: false);
return 0;

internal static class Scenario
{
    public const string Tenant = "contoso";
    public const string RideSharing = "Are ride-sharing costs reimbursable on business travel?";
    public const string Crypto = "What is the policy on cryptocurrency investments?";
    private static int s_passed, s_total;
    private static bool s_assert;

    public static PermissionDirectory Directory()
    {
        PermissionDirectory directory = new();
        directory.Grant(Tenant, "E-1001", Permissions.SubmitReimbursement);
        directory.Grant(Tenant, "E-2002", Permissions.SubmitReimbursement);
        directory.Grant("fabrikam", "mallory");
        return directory;
    }

    public static void Check(bool ok, string name)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine(s_assert ? $"  {(ok ? "PASS" : "FAIL")}  {name}" : $"  {(ok ? "as expected" : "differs    ")}  {name}");
    }

    public static int Result()
    {
        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    public static void Show(FaqReply r)
    {
        Console.WriteLine($"  {r.Subject} ({r.Tenant}): {r.Question}");
        Console.WriteLine($"  -> {r.Outcome}: {r.Text.ReplaceLineEndings(" ")}");
        Console.WriteLine($"     evidence [{string.Join(", ", r.Evidence.Select(e => $"{e.ChunkId} {e.Score:F2}"))}]" +
                          $" cited [{string.Join(", ", r.Cited)}]");
    }

    /// <summary>The three steps every run shows, live or scripted.</summary>
    public static async Task RunAsync(FaqDesk desk, bool assert)
    {
        s_assert = assert;

        Console.WriteLine("\n-- 1. Alice asks before she files her taxi claim --");
        FaqReply ride = await desk.AskAsync(Tenant, "E-1001", RideSharing);
        Show(ride);
        Check(ride.Outcome == FaqOutcome.Answered && ride.Cited.Contains("expense-policy")
              && ride.Evidence.Any(e => e.Source == "expense-policy"),
            "answered, citing the expense policy that was retrieved");

        Console.WriteLine("\n-- 2. Bob asks about something the handbook does not cover --");
        FaqReply crypto = await desk.AskAsync(Tenant, "E-2002", Crypto);
        Show(crypto);
        Check(crypto.Outcome == FaqOutcome.NoEvidence && crypto.Evidence.Count == 0,
            "abstained: nothing scored at or above the threshold, the model was not called");

        Console.WriteLine("\n-- 3. A caller of another tenant --");
        FaqReply other = await desk.AskAsync("fabrikam", "mallory", "How many PTO days do new employees get?");
        Show(other);
        Check(other.Outcome == FaqOutcome.NotPermitted && other.Evidence.Count == 0,
            "refused before any search: the handbook is Contoso's");

        if (!assert) Console.WriteLine($"\nEvidence log: {desk.EvidenceLog.Count} records.");
    }
}

internal static partial class SelfTest
{
    private static readonly (string Question, string Source)[] Concurrent =
    [
        ("How many PTO days do new employees get?", "pto-policy"),
        (Scenario.RideSharing, "expense-policy"),
        ("What is the home office equipment stipend for remote staff?", "remote-work"),
        ("How long is parental leave for birth parents?", "benefits"),
        ("Can unused PTO carry over?", "pto-policy"),
        ("Are meal expenses capped on travel?", "expense-policy"),
    ];

    public static async Task<int> RunAsync()
    {
        Keywords embeddings = new();
        InMemoryCollection<string, FaqChunk> collection = new("handbook-selftest");
        await FaqIngest.IngestAsync(embeddings, collection, Corpus());
        FaqSearchService search = new(embeddings, collection, topK: 3, scoreThreshold: 0.55);
        PermissionDirectory directory = Scenario.Directory();

        ScriptedModel honest = new(ScriptedModel.Mode.Honest);
        FaqDesk desk = new(honest, search, directory, Scenario.Tenant);
        await Scenario.RunAsync(desk, assert: true);
        Scenario.Check(honest.Questions.Count == 1, $"the model was called once, for Alice only ({honest.Questions.Count} call)");

        Console.WriteLine("\n-- 4. Models that do not back their answer --");
        foreach ((ScriptedModel.Mode mode, FaqOutcome expected) in new[]
                 {
                     (ScriptedModel.Mode.NoCitation, FaqOutcome.Uncited),
                     (ScriptedModel.Mode.CitesOther, FaqOutcome.UnsupportedCitation),
                     (ScriptedModel.Mode.Declines, FaqOutcome.Declined),
                 })
        {
            FaqDesk faulty = new(new ScriptedModel(mode), search, directory, Scenario.Tenant);
            FaqReply r = await faulty.AskAsync(Scenario.Tenant, "E-1001", Scenario.RideSharing);
            Console.WriteLine($"  {mode}: {r.Outcome}, shown \"{r.Text}\", cited [{string.Join(", ", r.Cited)}]");
            string expectedText = expected == FaqOutcome.Declined ? FaqDesk.Abstention : FaqDesk.Withheld;
            Scenario.Check(r.Outcome == expected && r.Text == expectedText,
                $"{mode}: {expected}, the model's text is not shown");
        }

        Console.WriteLine($"\n-- 5. {Concurrent.Length} questions at the same moment on one desk --");
        embeddings.Reset(rendezvous: Concurrent.Length);
        FaqReply[] replies = await Task.WhenAll(Concurrent.Select(q =>
            Task.Run(() => desk.AskAsync(Scenario.Tenant, "E-1001", q.Question))));
        int own = replies.Where((r, i) => r.Outcome == FaqOutcome.Answered
                                          && r.Cited.SequenceEqual([Concurrent[i].Source])
                                          && r.Evidence.All(e => e.Source == Concurrent[i].Source)).Count();
        Scenario.Check(embeddings.RendezvousReached, $"all {Concurrent.Length} searches were in flight at the same time");
        Scenario.Check(own == Concurrent.Length, $"each reply holds only its own evidence and citation ({own}/{Concurrent.Length})");

        Console.WriteLine("\n-- 6. The evidence log --");
        Scenario.Check(desk.EvidenceLog.Count == 3 + Concurrent.Length
                       && desk.EvidenceLog.Select(r => r.QuestionId).Distinct().Count() == desk.EvidenceLog.Count,
            $"one record per question, each with its own id ({desk.EvidenceLog.Count} records)");
        return Scenario.Result();
    }

    private static List<FaqChunk> Corpus() =>
    [
        new() { Id = "pto-policy-2", Source = "pto-policy", Topic = "PTO", Content = "New employees get 15 PTO days per year; up to 5 unused days carry over." },
        new() { Id = "expense-policy-6", Source = "expense-policy", Topic = "Expenses", Content = "Meal expenses on travel are capped at $75 per day domestic and $100 international." },
        new() { Id = "expense-policy-8", Source = "expense-policy", Topic = "Expenses", Content = "Airfare, hotel, rental car, and ride-sharing expenses are reimbursable on travel." },
        new() { Id = "remote-work-6", Source = "remote-work", Topic = "Remote work", Content = "Remote staff get a $500 home office equipment stipend." },
        new() { Id = "benefits-6", Source = "benefits", Topic = "Benefits", Content = "Birth parents receive 16 weeks of fully paid parental leave." },
    ];

    /// <summary>One axis per keyword plus an "anything else" axis; can hold searches until N are in flight.</summary>
    private sealed partial class Keywords : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly string[] Axes = ["pto", "expense", "remote", "parental"];
        private TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rendezvous, _arrived;

        public bool RendezvousReached => _all.Task.IsCompletedSuccessfully;

        public void Reset(int rendezvous)
        {
            _rendezvous = rendezvous;
            _arrived = 0;
            _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            string[] texts = [.. values];
            if (texts.Length == 1 && _rendezvous > 0)
            {
                if (Interlocked.Increment(ref _arrived) >= _rendezvous) _all.TrySetResult();
                await Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
            }
            return [.. texts.Select(Embed)];
        }

        private static Embedding<float> Embed(string text)
        {
            float[] v = new float[FaqChunk.Dimensions];
            string lower = text.ToLowerInvariant();
            string[] words = Word().Split(lower);
            for (int i = 0; i < Axes.Length; i++)
                if (words.Any(w => w.StartsWith(Axes[i], StringComparison.Ordinal))) v[i] = 1f;
            // Synonyms the questions use: travel costs are expenses, meals are expenses.
            if (lower.Contains("ride-sharing", StringComparison.Ordinal) || lower.Contains("meal", StringComparison.Ordinal)) v[1] = 1f;
            if (v.All(x => x == 0)) v[^1] = 1f;
            return new Embedding<float>(v);
        }

        [GeneratedRegex("[^a-z]+")] private static partial Regex Word();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>A scripted model that reads the "[Source: x]" tags of the excerpts it was given.</summary>
    private sealed partial class ScriptedModel(ScriptedModel.Mode mode) : IChatClient
    {
        public enum Mode { Honest, NoCitation, CitesOther, Declines }

        public ConcurrentQueue<string> Questions { get; } = new();

        [GeneratedRegex(@"\[Source: ([a-z0-9-]+)\]")] private static partial Regex Tag();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatMessage[] all = [.. messages];
            Questions.Enqueue(all[^1].Text);
            string[] sources = [.. all.SelectMany(m => Tag().Matches(m.Text)).Select(m => m.Groups[1].Value).Distinct()];
            string text = mode switch
            {
                Mode.NoCitation => "Yes, that is covered.",
                Mode.CitesOther => "Yes, that is covered [benefits].",
                Mode.Declines => FaqDesk.Abstention,
                _ => "Per the handbook: " + string.Join(" ", sources.Select(s => $"[{s}]")),
            };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
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
}
