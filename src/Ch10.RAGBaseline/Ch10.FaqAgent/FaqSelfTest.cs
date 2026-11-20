using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using CommunityToolkit.VectorData.InMemory;

namespace Ch10.FaqAgent;

/// <summary>
/// Offline checks of the FAQ agent's wiring: dotnet run -- --self-test.
/// A keyword embedding generator and a fake model stand in for both models.
/// The fake model follows the grounding rule by construction, so these checks
/// prove what reached the model and what was retrieved, not how a real model
/// uses the evidence.
/// </summary>
internal static partial class FaqSelfTest
{
    private const string Pto = "How many PTO days do new employees get?";
    private const string Remote = "What is the remote work equipment stipend?";
    private const string FollowUp = "What about for contractors?";
    private const string Expense = "How do I file an expense report?";
    private const string Crypto = "What is the policy on cryptocurrency investments?";

    private static readonly (string Question, string Source)[] Concurrent =
    [
        (Pto, "pto-policy"), (Remote, "remote-work"), (Expense, "expense-policy"),
        ("How long is parental leave for birth parents?", "benefits"),
        ("Can I carry over unused PTO days?", "pto-policy"), ("Which expense needs pre-approval?", "expense-policy"),
        ("Who pays for my remote monitor?", "remote-work"), ("Is parental leave paid?", "benefits"),
    ];

    private static int s_passed, s_total;

    private static void Check(bool ok, string name)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Contoso FAQ agent: self-test (fake model, keyword embeddings) ===");
        KeywordEmbeddings embeddings = new();
        InMemoryCollection<string, FaqChunk> collection = new("faq-selftest");
        await FaqIngest.IngestAsync(embeddings, collection, Corpus());
        FaqSearchService search = new(embeddings, collection, topK: 3, scoreThreshold: 0.55);
        FakeModel model = new();

        Console.WriteLine("\n-- 1. Ingest: the dimension check runs before any document --");
        string failure = "";
        try { await FaqIngest.CheckModelAsync(new KeywordEmbeddings(dimensions: 1536)); }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        Check(failure.StartsWith("The embedding model returned 1536 dimensions, but FaqChunk declares 768", StringComparison.Ordinal),
            "a 1536-dimension model is refused at startup");

        Console.WriteLine("\n-- 2. TextSearchProvider, search before each call --");
        ChatClientAgent agent = FaqAgentFactory.Create(model, search, RetrievalMode.TextSearch);
        AgentSession session = await agent.CreateSessionAsync();
        embeddings.Reset();
        string first = (await agent.RunAsync(Pto, session)).Text;
        Console.WriteLine($"  injected before the call: {model.LastEvidence.Count} message(s), role {model.LastEvidenceRole}");
        Console.WriteLine($"  first line: {model.LastEvidence.FirstOrDefault()?.Split('\n')[0]}");
        Check(embeddings.Queries.SequenceEqual([Pto]), "the search input is the caller's question");
        Check(first == "answer from [pto-policy]", $"the answer cites the retrieved source -> {first}");
        Check(model.LastEvidence.Any(e => e.Contains("15 PTO days", StringComparison.Ordinal)),
            "the chunk text reached the model");

        embeddings.Reset();
        string remote = (await agent.RunAsync(Remote, session)).Text;
        string remoteInput = embeddings.Queries.Single();
        embeddings.Reset();
        string followUp = (await agent.RunAsync(FollowUp, session)).Text;
        string input = embeddings.Queries.Single();
        Console.WriteLine($"  follow-up search input: \"{input.ReplaceLineEndings(" | ")}\"");
        Check(input.Contains(Pto, StringComparison.Ordinal) && input.Contains(Remote, StringComparison.Ordinal)
              && !input.Contains("answer from", StringComparison.Ordinal),
            "RecentMessageMemoryLimit = 6: the search input holds the earlier user questions, not the answers");
        Check(followUp.Contains("[remote-work]", StringComparison.Ordinal),
            $"the follow-up retrieves the remote-work chunks -> {followUp}");
        Check(remoteInput.Contains(Pto, StringComparison.Ordinal) && remote == "answer from [pto-policy] [remote-work]",
            $"side effect: the earlier topic rides along into a new question -> {remote}");

        ChatClientAgent noMemory = new(model, new ChatClientAgentOptions
        {
            AIContextProviders = [new TextSearchProvider(search.SearchAsync)],
            ChatOptions = new() { Instructions = FaqAgentFactory.Instructions },
        });
        AgentSession bare = await noMemory.CreateSessionAsync();
        await noMemory.RunAsync(Remote, bare);
        embeddings.Reset();
        string lost = (await noMemory.RunAsync(FollowUp, bare)).Text;
        Check(embeddings.Queries.SequenceEqual([FollowUp]) && lost == FaqAgentFactory.Abstention,
            "with the default options the same follow-up searches alone and abstains");

        embeddings.Reset();
        string crypto = (await agent.RunAsync(Crypto, await agent.CreateSessionAsync())).Text;
        Check(crypto == FaqAgentFactory.Abstention && model.LastEvidence.Count == 0,
            "an out-of-handbook question: no hit above 0.55, nothing injected, abstention");

        Console.WriteLine("\n-- 3. TextSearchProvider on demand: a tool, not a search before the call --");
        ChatClientAgent onDemand = FaqAgentFactory.Create(model, search, RetrievalMode.OnDemand);
        embeddings.Reset();
        await onDemand.RunAsync(Pto, await onDemand.CreateSessionAsync());
        Check(model.LastToolNames.SequenceEqual(["search_handbook"]) && embeddings.Queries.Count == 0,
            "the model is offered search_handbook and nothing was searched before the call");

        Console.WriteLine("\n-- 4. Which context carries the caller's message --");
        ProbeContext probeContext = new();
        ProbeMessages probeMessages = new();
        ChatClientAgent probed = new(model, new ChatClientAgentOptions
        {
            AIContextProviders = [probeContext, probeMessages, new FaqContextProvider(search)],
            ChatOptions = new() { Instructions = FaqAgentFactory.Instructions },
        });
        AgentSession probedSession = await probed.CreateSessionAsync();
        await probed.RunAsync(Pto, probedSession);
        probeContext.Seen.Clear();
        probeMessages.Seen.Clear();
        string second = (await probed.RunAsync(Remote, probedSession)).Text;
        Console.WriteLine($"  turn 2, AIContextProvider.InvokingContext.AIContext.Messages: [{string.Join(" | ", probeContext.Seen)}]");
        Console.WriteLine($"  turn 2, MessageAIContextProvider.InvokingContext.RequestMessages: [{string.Join(" | ", probeMessages.Seen)}]");
        Check(probeMessages.Seen.SequenceEqual([$"user: {Remote}"]),
            "RequestMessages holds this call's question only (no history)");
        Check(probeContext.Seen.Contains($"user: {Remote}"), "AIContext.Messages also carries it");
        Check(second == "answer from [remote-work]", $"FaqContextProvider retrieves for the latest question -> {second}");

        foreach (string blank in new[] { "", "   " })
        {
            embeddings.Reset();
            string outcome;
            try
            {
                outcome = (await probed.RunAsync(new ChatMessage(ChatRole.User, blank), await probed.CreateSessionAsync())).Text;
            }
            catch (Exception ex) { outcome = $"threw {ex.GetType().Name}"; }
            Check(outcome == FaqAgentFactory.Abstention && embeddings.Queries.Count == 0,
                $"blank question \"{blank}\": no search, no throw, abstention");
        }

        Console.WriteLine("\n-- 5. The failure: a shared provider with a mutable CurrentQuery (the first edition's shape) --");
        MutableQueryProvider v1 = new(search);
        ChatClientAgent v1Agent = new(model, new ChatClientAgentOptions
        {
            AIContextProviders = [v1],
            ChatOptions = new() { Instructions = FaqAgentFactory.Instructions },
        });
        using Barrier bothSet = new(2);
        async Task<string> AskV1(string question)
        {
            AgentSession s = await v1Agent.CreateSessionAsync();
            v1.CurrentQuery = question;          // "set this before each RunAsync"
            bothSet.SignalAndWait(TimeSpan.FromSeconds(5));
            return (await v1Agent.RunAsync(question, s)).Text;
        }
        Task<string> a = Task.Run(() => AskV1(Pto));
        Task<string> b = Task.Run(() => AskV1(Expense));
        string[] v1Answers = await Task.WhenAll(a, b);
        Console.WriteLine($"  A asked about PTO      -> {v1Answers[0]}");
        Console.WriteLine($"  B asked about expenses -> {v1Answers[1]}");
        Check(v1Answers[0] != "answer from [pto-policy]" || v1Answers[1] != "answer from [expense-policy]",
            "DEFECT REPRODUCED: one request answered from the other request's chunks");

        Console.WriteLine($"\n-- 6. The fix: {Concurrent.Length} concurrent questions on one shared agent --");
        foreach (RetrievalMode mode in new[] { RetrievalMode.Custom, RetrievalMode.TextSearch })
        {
            ChatClientAgent shared = FaqAgentFactory.Create(model, search, mode);
            embeddings.Reset(rendezvous: Concurrent.Length);
            Task<string>[] calls = [.. Concurrent.Select(q => Task.Run(async () =>
                (await shared.RunAsync(q.Question, await shared.CreateSessionAsync())).Text))];
            string[] answers = await Task.WhenAll(calls);
            int own = answers.Where((t, i) => t == $"answer from [{Concurrent[i].Source}]").Count();
            Check(embeddings.RendezvousReached, $"{mode}: all {Concurrent.Length} searches were in flight at the same time");
            Check(own == Concurrent.Length, $"{mode}: each call saw only its own chunks ({own}/{Concurrent.Length})");
        }

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    private static List<FaqChunk> Corpus() =>
    [
        new() { Id = "pto-policy-0", Source = "pto-policy", Topic = "PTO", Content = "New employees get 15 PTO days per year; up to 5 unused days carry over." },
        new() { Id = "remote-work-0", Source = "remote-work", Topic = "Remote work", Content = "Remote staff get a $500 home office equipment stipend." },
        new() { Id = "remote-work-1", Source = "remote-work", Topic = "Remote work", Content = "Contractors may request remote work with manager approval." },
        new() { Id = "expense-policy-0", Source = "expense-policy", Topic = "Expenses", Content = "File every expense report in the portal within 30 days; over $100 needs pre-approval." },
        new() { Id = "benefits-0", Source = "benefits", Topic = "Benefits", Content = "Birth parents receive 16 weeks of fully paid parental leave." },
    ];

    /// <summary>The first edition's provider: the caller sets CurrentQuery before each RunAsync.</summary>
    private sealed class MutableQueryProvider(FaqSearchService search) : AIContextProvider
    {
        public string? CurrentQuery { get; set; }

        protected override async ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(CurrentQuery)) return new AIContext();
            IReadOnlyList<FaqHit> hits = await search.RetrieveAsync(CurrentQuery, cancellationToken);
            return hits.Count == 0 ? new AIContext()
                : new AIContext { Messages = [new ChatMessage(ChatRole.User, FaqContextProvider.FormatEvidence(hits))] };
        }
    }

    private sealed class ProbeContext : AIContextProvider
    {
        public ConcurrentQueue<string> Seen { get; } = new();

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken)
        {
            foreach (ChatMessage m in context.AIContext.Messages ?? []) Seen.Enqueue($"{m.Role}: {m.Text}");
            return ValueTask.FromResult(new AIContext());
        }
    }

    private sealed class ProbeMessages : MessageAIContextProvider
    {
        public ConcurrentQueue<string> Seen { get; } = new();

        protected override ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(InvokingContext context, CancellationToken cancellationToken)
        {
            foreach (ChatMessage m in context.RequestMessages) Seen.Enqueue($"{m.Role}: {m.Text}");
            return ValueTask.FromResult<IEnumerable<ChatMessage>>([]);
        }
    }

    /// <summary>
    /// One axis per topic keyword, plus an "anything else" axis: cosine is 1 on
    /// topic and 0 off it. Text in square brackets (citations) is ignored.
    /// Records each single-text request (a search) and can hold searches until
    /// N are in flight, so a concurrency check really overlaps.
    /// </summary>
    private sealed partial class KeywordEmbeddings(int dimensions = FaqChunk.Dimensions)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly string[] Keywords = ["pto", "remote", "expense", "parental"];
        private readonly ConcurrentQueue<string> _queries = new();
        private TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rendezvous, _arrived;

        public IReadOnlyList<string> Queries => [.. _queries];
        public bool RendezvousReached => _all.Task.IsCompletedSuccessfully;

        public void Reset(int rendezvous = 0)
        {
            _queries.Clear();
            _rendezvous = rendezvous;
            _arrived = 0;
            _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            string[] texts = [.. values];
            if (texts.Length == 1)
            {
                _queries.Enqueue(texts[0]);
                if (_rendezvous > 0)
                {
                    if (Interlocked.Increment(ref _arrived) >= _rendezvous) _all.TrySetResult();
                    await Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                }
            }
            return [.. texts.Select(Embed)];
        }

        private Embedding<float> Embed(string text)
        {
            float[] v = new float[dimensions];
            string[] words = Word().Split(Bracketed().Replace(text.ToLowerInvariant(), " "));
            bool any = false;
            for (int i = 0; i < Keywords.Length; i++)
            {
                if (words.Any(w => w.StartsWith(Keywords[i], StringComparison.Ordinal))) { v[i] = 1f; any = true; }
            }
            if (!any) v[dimensions - 1] = 1f;
            return new Embedding<float>(v);
        }

        [GeneratedRegex(@"\[[^\]]*\]")] private static partial Regex Bracketed();
        [GeneratedRegex("[^a-z]+")] private static partial Regex Word();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Fake model. It looks at the messages of this turn (after the last
    /// assistant message): if a handbook chunk is there, it answers "answer from
    /// [source]..."; if none is, it gives the abstention text.
    /// </summary>
    private sealed class FakeModel : IChatClient
    {
        private static readonly Dictionary<string, string> ChunkSource = Corpus().ToDictionary(c => c.Content, c => c.Source);

        public IReadOnlyList<string> LastEvidence { get; private set; } = [];
        public ChatRole? LastEvidenceRole { get; private set; }
        public IReadOnlyList<string> LastToolNames { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatMessage> all = [.. messages];
            int lastAssistant = all.FindLastIndex(m => m.Role == ChatRole.Assistant);
            List<ChatMessage> turn = all.Skip(lastAssistant + 1).ToList();
            List<ChatMessage> evidence = turn.Where(m => ChunkSource.Keys.Any(c => m.Text.Contains(c, StringComparison.Ordinal))).ToList();
            string[] cited = [.. ChunkSource.Where(kv => evidence.Any(m => m.Text.Contains(kv.Key, StringComparison.Ordinal)))
                .Select(kv => kv.Value).Distinct().Order(StringComparer.Ordinal)];

            LastEvidence = [.. evidence.Select(m => m.Text)];
            LastEvidenceRole = evidence.FirstOrDefault()?.Role;
            LastToolNames = [.. (options?.Tools ?? []).Select(t => t.Name)];

            string text = cited.Length == 0 ? FaqAgentFactory.Abstention
                : "answer from " + string.Join(" ", cited.Select(s => $"[{s}]"));
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
