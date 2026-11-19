using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using CommunityToolkit.VectorData.InMemory;

namespace Ch10.FaqAgent.CustomProvider;

/// <summary>
/// Offline check of <see cref="RagContextProvider"/>: dotnet run -- --self-test.
/// No LLM and no embedding model: a keyword embedding generator and an echo chat
/// client stand in for both, so the test proves the retrieval wiring only.
/// </summary>
internal static partial class RagSelfTest
{
    private const string PtoQuestion = "How many PTO days do new employees get?";
    private const string RemoteQuestion = "What about remote work equipment?";
    private const string ExpenseQuestion = "How do I file an expense report?";

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Chapter 10: RagContextProvider self-test (offline) ===\n");

        int passed = 0, total = 0;
        void Check(bool ok, string name, string detail)
        {
            total++;
            passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name,-52} {detail}");
        }

        KeywordEmbeddingGenerator embeddings = new();
        InMemoryCollection<string, FaqChunk> collection = await SeedAsync(embeddings);

        // ONE provider and ONE agent, shared by every call below: the DI-singleton shape
        // Chapters 14 and 16 register. Any per-call state on the provider would leak.
        ProbeProvider probe = new();
        RagContextProvider rag = new(collection, embeddings, topK: 3);
        ChatClientAgent agent = new(new EchoChatClient(), new ChatClientAgentOptions
        {
            Name = "SelfTestFaqAgent",
            AIContextProviders = [probe, rag],
        });

        // 1. The query comes from the request message, not from a property.
        AgentSession session = await agent.CreateSessionAsync();
        embeddings.Reset();
        AgentResponse first = await agent.RunAsync(PtoQuestion, session);
        string[] seen = probe.Take();
        Check(seen.Contains(PtoQuestion), "InvokingContext.AIContext carries the user message",
            $"saw [{string.Join(" | ", seen)}]");
        Check(embeddings.Queries.SequenceEqual([PtoQuestion]), "provider embeds exactly the request text",
            $"embedded [{string.Join(" | ", embeddings.Queries)}]");
        Check(first.Text == "sources=pto-policy", "first turn retrieves the PTO chunk", first.Text);

        // 2. Follow-up in the same session: the LATEST user message drives retrieval.
        embeddings.Reset();
        AgentResponse second = await agent.RunAsync(RemoteQuestion, session);
        string[] seenFollowUp = probe.Take();
        Check(embeddings.Queries.SequenceEqual([RemoteQuestion]), "follow-up embeds the latest user message",
            $"saw [{string.Join(" | ", seenFollowUp)}]");
        Check(second.Text == "sources=remote-work", "follow-up retrieves the remote-work chunk", second.Text);

        // 3. Two concurrent RunAsync calls on the shared agent/provider. The embedding
        //    generator holds each call until both have arrived, so they truly overlap.
        embeddings.Reset(rendezvous: 2);
        Task<AgentResponse> a = agent.RunAsync(PtoQuestion, await agent.CreateSessionAsync());
        Task<AgentResponse> b = agent.RunAsync(ExpenseQuestion, await agent.CreateSessionAsync());
        AgentResponse[] both = await Task.WhenAll(a, b);
        probe.Take();
        Check(embeddings.RendezvousReached, "both calls were in flight at the same time",
            $"embedded [{string.Join(" | ", embeddings.Queries.Order())}]");
        Check(both[0].Text == "sources=pto-policy", "concurrent call A gets only its own chunk", both[0].Text);
        Check(both[1].Text == "sources=expenses", "concurrent call B gets only its own chunk", both[1].Text);

        // 4. Empty / whitespace question: nothing retrieved, nothing thrown. RunAsync(string)
        //    itself rejects a blank string, so the blank text arrives as a ChatMessage,
        //    which is how it would reach the provider from a caller that skips validation.
        foreach (string blank in new[] { "", "   " })
        {
            embeddings.Reset();
            string outcome;
            bool ok;
            try
            {
                AgentResponse r = await agent.RunAsync(
                    new ChatMessage(ChatRole.User, blank), await agent.CreateSessionAsync());
                ok = r.Text == "sources=none" && embeddings.Queries.Count == 0;
                outcome = $"{r.Text}, embed calls={embeddings.Queries.Count}";
            }
            catch (Exception ex)
            {
                ok = false;
                outcome = $"threw {ex.GetType().Name}: {ex.Message}";
            }
            probe.Take();
            Check(ok, $"blank question \"{blank}\" retrieves nothing, no throw", outcome);
        }

        // 5. Off-topic question: embedded, but every hit is under the score threshold.
        embeddings.Reset();
        AgentResponse offTopic = await agent.RunAsync(
            "What is the policy on cryptocurrency investments?", await agent.CreateSessionAsync());
        Check(offTopic.Text == "sources=none", "off-topic question falls below the threshold", offTopic.Text);

        Console.WriteLine($"RESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static async Task<InMemoryCollection<string, FaqChunk>> SeedAsync(
        IEmbeddingGenerator<string, Embedding<float>> embeddings)
    {
        InMemoryCollection<string, FaqChunk> collection = new("faq-selftest");
        await collection.EnsureCollectionExistsAsync();

        FaqChunk[] chunks =
        [
            new() { Id = "pto-0", Source = "pto-policy", Topic = "PTO", Content = "New employees accrue 15 PTO days per year." },
            new() { Id = "remote-0", Source = "remote-work", Topic = "Remote work", Content = "Remote staff get a $500 home office equipment stipend." },
            new() { Id = "expense-0", Source = "expenses", Topic = "Expenses", Content = "File every expense report in the portal within 30 days." },
        ];

        GeneratedEmbeddings<Embedding<float>> vectors =
            await embeddings.GenerateAsync(chunks.Select(c => c.Content));
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i].Embedding = FaqChunk.CheckDimensions(vectors[i].Vector);
            await collection.UpsertAsync(chunks[i]);
        }
        return collection;
    }

    /// <summary>
    /// Deterministic stand-in for an embedding model: one axis per topic keyword,
    /// plus an "anything else" axis, so cosine similarity is 1 on topic and 0 off it.
    /// Records every single-text (query) request it receives.
    /// </summary>
    private sealed class KeywordEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly string[] Keywords = ["pto", "remote", "expense"];
        private readonly ConcurrentQueue<string> _queries = new();
        private TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _rendezvous;
        private int _arrived;

        public IReadOnlyCollection<string> Queries => _queries.ToArray();
        public bool RendezvousReached => _allArrived.Task.IsCompletedSuccessfully;

        public void Reset(int rendezvous = 0)
        {
            _queries.Clear();
            _rendezvous = rendezvous;
            _arrived = 0;
            _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string[] texts = values.ToArray();
            if (texts.Length == 1)
            {
                _queries.Enqueue(texts[0]);
                if (_rendezvous > 0)
                {
                    if (Interlocked.Increment(ref _arrived) >= _rendezvous) _allArrived.TrySetResult();
                    // Wait for the other call; give up after 5 s so a broken run fails, not hangs.
                    await Task.WhenAny(_allArrived.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
                }
            }
            return [.. texts.Select(Embed)];
        }

        private static Embedding<float> Embed(string text)
        {
            float[] vector = new float[FaqChunk.Dimensions];
            // Whole-word prefix match, so "expenses" hits "expense" but "crypto" misses "pto".
            string[] words = Regex.Split(text.ToLowerInvariant(), "[^a-z]+");
            bool any = false;
            for (int i = 0; i < Keywords.Length; i++)
            {
                if (words.Any(w => w.StartsWith(Keywords[i], StringComparison.Ordinal))) { vector[i] = 1f; any = true; }
            }
            if (!any) vector[FaqChunk.Dimensions - 1] = 1f;
            return new Embedding<float>(vector);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Records the message texts each invocation hands to a context provider.</summary>
    private sealed class ProbeProvider : AIContextProvider
    {
        private readonly ConcurrentQueue<string> _seen = new();

        public string[] Take()
        {
            string[] seen = _seen.ToArray();
            _seen.Clear();
            return seen;
        }

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken)
        {
            foreach (ChatMessage m in context.AIContext.Messages ?? [])
            {
                // User messages verbatim; anything else tagged with its role.
                _seen.Enqueue(m.Role == ChatRole.User ? m.Text : $"{m.Role}: {m.Text}");
            }
            return ValueTask.FromResult(new AIContext());
        }
    }

    /// <summary>
    /// Fake model: answers with the sources it found in its prompt ("sources=a,b" or
    /// "sources=none"), so each caller can see exactly which chunks reached its call.
    /// </summary>
    private sealed partial class EchoChatClient : IChatClient
    {
        [GeneratedRegex(@"\[Source: ([^,\]]+)")]
        private static partial Regex SourceTag();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string prompt = (options?.Instructions ?? "") + "\n" + string.Join("\n", messages.Select(m => m.Text));
            string[] sources = SourceTag().Matches(prompt).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
            string text = "sources=" + (sources.Length == 0 ? "none" : string.Join(",", sources));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
