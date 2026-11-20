using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using CommunityToolkit.VectorData.InMemory;

namespace Ex02.DocumentQA;

/// <summary>
/// Offline checks: dotnet run -- --self-test. Keyword embeddings and a fake
/// model; the fake cites what it was given, so the checks prove the wiring
/// (what was retrieved, whether the model was called, whether citations match).
/// </summary>
internal static partial class SelfTest
{
    private static readonly Dictionary<string, string> Docs = new()
    {
        ["about"] = "NovaTech was founded in 2020 in Seattle by CEO Sarah Chen.",
        ["products"] = "CodePilot supports C#, Python, and TypeScript.",
        ["financials"] = "NovaTech reported $45M in annual revenue for 2025.",
    };

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Document Q&A: self-test (fake model, keyword embeddings) ===\n");
        int passed = 0, total = 0;
        void Check(bool ok, string name)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        async Task<string> DimensionCheck(int configured, int modelSize)
        {
            try { await DocumentQa.CheckDimensionsAsync(configured, new Keywords(modelSize)); return "ok"; }
            catch (InvalidOperationException ex) { return ex.Message; }
        }
        string config1536 = await DimensionCheck(1536, 768);
        string model1536 = await DimensionCheck(768, 1536);
        Console.WriteLine($"  {config1536}\n  {model1536}");
        Check(config1536.StartsWith("EmbeddingDimensions is 1536", StringComparison.Ordinal), "a configured size that differs from DocumentChunk is refused");
        Check(model1536.StartsWith("The embedding model returned 1536", StringComparison.Ordinal), "a model whose vectors differ is refused");
        Check(await DimensionCheck(768, 768) == "ok", "768 configured, declared and returned: passes");

        Keywords embeddings = new(DocumentChunk.Dimensions);
        InMemoryCollection<string, DocumentChunk> collection = new("selftest");
        await DocumentQa.IngestAsync(embeddings, collection, Docs);

        FakeModel honest = new(citeInstead: null);
        DocumentQa qa = new(embeddings, collection, new ChatClientAgent(honest, instructions: DocumentQa.Instructions), scoreThreshold: 0.55);
        QaAnswer founded = await qa.AskAsync("Who founded NovaTech?");
        Console.WriteLine($"\n  Q: Who founded NovaTech?  A: {founded.Text}");
        Check(founded.SourceIds.SequenceEqual(["about"]) && founded.CitesItsSources(Docs.Keys),
            "the answer cites the id of the retrieved document");

        int callsBefore = honest.Calls;
        QaAnswer dress = await qa.AskAsync("What is the dress code?");
        Console.WriteLine($"  Q: What is the dress code?  A: {dress.Text}");
        Check(dress.Abstained && dress.Text == DocumentQa.Abstention && honest.Calls == callsBefore,
            "an out-of-corpus question abstains and the model is not called");

        DocumentQa wrong = new(embeddings, collection,
            new ChatClientAgent(new FakeModel(citeInstead: "financials"), instructions: DocumentQa.Instructions), 0.55);
        QaAnswer miscited = await wrong.AskAsync("Which languages does CodePilot support?");
        Console.WriteLine($"  a model that cites a document it was not given: {miscited.Text}");
        Check(!miscited.CitesItsSources(Docs.Keys), "a citation of a document that was not retrieved is caught");

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    /// <summary>One axis per keyword plus an "anything else" axis.</summary>
    private sealed class Keywords(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly string[] Axes = ["founded", "codepilot", "revenue"];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<GeneratedEmbeddings<Embedding<float>>>([.. values.Select(text =>
            {
                float[] v = new float[dimensions];
                string lower = text.ToLowerInvariant();
                for (int i = 0; i < Axes.Length; i++) if (lower.Contains(Axes[i], StringComparison.Ordinal)) v[i] = 1f;
                if (v.All(x => x == 0)) v[dimensions - 1] = 1f;
                return new Embedding<float>(v);
            })]);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Cites the ids it finds in the context ("[id]: ..."), or always cites one fixed id.</summary>
    private sealed partial class FakeModel(string? citeInstead) : IChatClient
    {
        public int Calls { get; private set; }

        [GeneratedRegex(@"\[([a-z0-9-]+)\]:")] private static partial Regex ContextId();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            string prompt = string.Join("\n", messages.Select(m => m.Text));
            string[] ids = citeInstead is null
                ? [.. ContextId().Matches(prompt).Select(m => m.Groups[1].Value)]
                : [citeInstead];
            string text = "Answer from " + string.Join(" ", ids.Select(id => $"[{id}]"));
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
