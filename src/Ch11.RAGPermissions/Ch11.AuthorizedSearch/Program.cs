// ============================================================================
// Authorized search: evidence filtered by tenant and level before anything sees it
// ----------------------------------------------------------------------------
// The FAQ agent of the RAG baseline, with a search boundary in front of the
// index. The host authenticates the caller and builds the agent for that one
// request; the search delegate filters inside the store query (tenant, access
// level, active state), returns evidence records with stable ids, and records
// them in the request's ledger. The answer's citations are checked against that
// ledger before it is delivered.
//
// The corpus holds two organizations (contoso, fabrikam), three access levels,
// superseded and revoked versions, a management draft and a hostile annex.
//
//   dotnet run                    Ollama: llama3.2 answers, nomic-embed-text embeds
//   dotnet run -- --self-test     scripted model, hashing embedder, no network;
//                                 RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.Net;
using System.Runtime.CompilerServices;
using AgenticAI.Common.Extensions;
using Ch11.AuthorizedSearch;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Authorized search: self-test ==="
    : "=== Authorized search (live: Ollama) ===");
return selfTest ? await SelfTest.RunAsync() : await Live.RunAsync(args);

internal static class Callers
{
    public static readonly CallerContext Alice = new("contoso", "E-1001", AccessLevel.Staff);
    public static readonly CallerContext Maria = new("contoso", "M-3003", AccessLevel.Manager);
    public static readonly CallerContext Xavier = new("contoso", "X-4004", AccessLevel.Restricted);
    public static readonly CallerContext Frank = new("fabrikam", "F-1", AccessLevel.Staff);
    public static readonly CallerContext Lena = new("fabrikam", "F-2", AccessLevel.Manager);
    public static readonly CallerContext Bruno = new("fabrikam", "F-3", AccessLevel.Restricted);
    public static readonly CallerContext[] All = [Alice, Maria, Xavier, Frank, Lena, Bruno];
}

internal static class Live
{
    public static async Task<int> RunAsync(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddLlmChatClient(builder.Configuration);
        using IHost host = builder.Build();
        IChatClient chat = host.Services.GetRequiredService<IChatClient>();
        IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
        if (!string.Equals(config["LlmProvider:Provider"], "ollama", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The live run embeds with Ollama (nomic-embed-text); set LlmProvider:Provider to ollama.");
            return 2;
        }
        using var embedder = new OllamaSharp.OllamaApiClient(
            new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text");

        // A threshold belongs to one embedding model and one corpus: 0.5 is a
        // starting point for nomic-embed-text here, not a probability.
        double minScore = double.Parse(config["Retrieval:MinScore"] ?? "0.5", System.Globalization.CultureInfo.InvariantCulture);
        var search = new AuthorizedSearchService(embedder, new InMemoryCollection<string, PolicyChunk>("policies"), 3, minScore);
        await search.IndexAsync(Corpus.Load());

        (CallerContext Caller, string Question)[] asks =
        [
            (Callers.Alice, "Can I expense a taxi home at 23:30 after a client dinner?"),
            (Callers.Frank, "Can I expense a taxi home at 23:30 after a client dinner?"),
            (Callers.Maria, "Can I expense a taxi home at 23:30 after a client dinner?"),
            (Callers.Alice, "Which taxi suppliers are in Annex C and what do they charge?"),
        ];
        foreach ((CallerContext caller, string question) in asks)
        {
            var ledger = new EvidenceLedger();
            ChatClientAgent agent = EvidenceAgent.ForRequest(chat, search, caller, ledger);
            AgentResponse response = await agent.RunAsync(question);
            CheckedAnswer checkedAnswer = EvidenceCheck.Check(response.Text, ledger);
            Console.WriteLine($"\n{caller.Tenant}/{caller.Level} {caller.Subject}: {question}");
            Console.WriteLine($"  evidence: {string.Join(", ", ledger.Issued.Select(e => e.EvidenceId))}");
            Console.WriteLine($"  answer:   {response.Text.ReplaceLineEndings(" ")}");
            Console.WriteLine(checkedAnswer.Accepted
                ? $"  check:    accepted, cites {string.Join(", ", checkedAnswer.Cited)}"
                : $"  check:    {checkedAnswer.Abstention}" +
                  (checkedAnswer.Rejected.Count > 0 ? $" (rejected {string.Join(", ", checkedAnswer.Rejected)})" : ""));
        }
        return 0;
    }
}

internal static class SelfTest
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        IReadOnlyList<PolicyDocument> corpus = Corpus.Load();
        var embedder = new HashingEmbedder();
        var search = new AuthorizedSearchService(embedder, new InMemoryCollection<string, PolicyChunk>("policies"));
        await search.IndexAsync(corpus);
        Console.WriteLine($"Indexed {corpus.Count} document versions (all states) from the two-organization corpus.");

        await ProbesAsync(search);
        await FailuresAsync(search, embedder, corpus);
        await MatrixAsync(search, corpus);
        await ConcurrencyAsync(search);
        await EvidenceCheckAsync(search);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    // ---- How the caller can reach the search delegate -------------------------
    static async Task ProbesAsync(AuthorizedSearchService search)
    {
        Console.WriteLine("\n-- Probes: where per-run data goes (MAF 1.28.0, OllamaSharp 5.4.25) --");
        var model = new ScriptedModel();
        string? seenInDelegate = null;
        TextSearchProvider provider = new(
            (query, ct) =>
            {
                seenInDelegate = AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties?["caller"] as string;
                return Task.FromResult<IEnumerable<TextSearchProvider.TextSearchResult>>([]);
            },
            new TextSearchProviderOptions { SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke });
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [provider] });
        await agent.RunAsync("taxi?", options: new AgentRunOptions { AdditionalProperties = new() { ["caller"] = "E-1001" } });
        Expect("AgentRunOptions.AdditionalProperties are readable in the search delegate (AIAgent.CurrentRunContext)",
            seenInDelegate == "E-1001");
        Expect("... and the same entries reach the chat client's ChatOptions.AdditionalProperties",
            model.LastOptions?.AdditionalProperties?.ContainsKey("caller") == true);

        var handler = new RecordingHandler();
        IChatClient ollama = new OllamaSharp.OllamaApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") }, "llama3.2");
        await ollama.GetResponseAsync("hi", new ChatOptions { AdditionalProperties = new() { ["caller"] = "E-1001" } });
        Expect("OllamaSharp leaves them out of the /api/chat request body (other connectors may not)",
            handler.LastBody.Contains("\"messages\"") && !handler.LastBody.Contains("E-1001"));

        var ledger = new EvidenceLedger();
        var faq = EvidenceAgent.ForRequest(model, search, Callers.Alice, ledger);
        await faq.RunAsync("Can I expense a taxi at night?");
        Expect("with a ContextFormatter, the injected context is the formatter's text alone",
            model.LastEvidenceMessage?.StartsWith("Evidence supplied for this question", StringComparison.Ordinal) == true);
    }

    // ---- First attempts that fail ---------------------------------------------
    static async Task FailuresAsync(AuthorizedSearchService search, HashingEmbedder embedder, IReadOnlyList<PolicyDocument> corpus)
    {
        Console.WriteLine("\n-- Where it fails: first attempts --");

        // 1. Rank everything, log the candidates, then drop what the caller may not read.
        List<string> log = [];
        List<string> kept = [];
        ReadOnlyMemory<float> query = embedder.Embed("taxi ride business trip receipt");
        await foreach (VectorSearchResult<PolicyChunk> hit in search.Store.SearchAsync(query, 5))
        {
            log.Add(hit.Record.EvidenceId);                     // a log, a reranker, a cache...
            if (hit.Record.Tenant == Callers.Alice.Tenant && hit.Record.Access <= (int)Callers.Alice.Level
                && hit.Record.State == nameof(SourceState.Active))
            {
                kept.Add(hit.Record.EvidenceId);
            }
        }
        string[] leaked = [.. log.Except(kept)];
        Console.WriteLine($"  post-filter log: {string.Join(", ", log)}");
        Expect($"DEFECT REPRODUCED: filter after ranking; the log saw {leaked.Length} chunks Alice may not read",
            leaked.Any(id => id.StartsWith("fabrikam-", StringComparison.Ordinal)) && leaked.Any(id => id.Contains("@v1#")));

        // 2. Tenant and level filtered, but not the state: a draft is "found".
        string tenant = Callers.Maria.Tenant;
        int level = (int)Callers.Maria.Level;
        List<string> noStateFilter = [];
        await foreach (VectorSearchResult<PolicyChunk> hit in search.Store.SearchAsync(
            embedder.Embed("taxi ride approval manager"), 3,
            new VectorSearchOptions<PolicyChunk> { Filter = c => c.Tenant == tenant && c.Access <= level }))
        {
            noStateFilter.Add(hit.Record.EvidenceId);
        }
        Expect($"DEFECT REPRODUCED: without the state filter Maria gets the draft as evidence ({noStateFilter[0]})",
            noStateFilter.Contains("contoso-taxi@v3#0"));
        IReadOnlyList<EvidenceRecord> fixedSearch = await search.SearchAsync(Callers.Maria, "taxi ride approval manager");
        Expect("with it, the draft is not evidence", fixedSearch.All(e => e.EvidenceId != "contoso-taxi@v3#0"));

        // 3. The caller in a field of a shared service, set by the host before the run.
        var shared = new SharedCallerSearch(search);
        var model = new ScriptedModel();
        TextSearchProvider sharedProvider = new(shared.SearchAsync,
            new TextSearchProviderOptions { SearchTime = TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke });
        var sharedAgent = new ChatClientAgent(model, new ChatClientAgentOptions { AIContextProviders = [sharedProvider] });
        var bothSet = new Barrier(2);
        async Task<IReadOnlyList<string>> Request(CallerContext caller)
        {
            shared.CurrentCaller = caller;
            bothSet.SignalAndWait();                // the other request sets the field here
            var seen = new List<string>();
            shared.Seen.Value = seen;
            await sharedAgent.RunAsync("Can I expense a taxi on a business trip?");
            return seen;
        }
        Task<IReadOnlyList<string>> a = Task.Run(() => Request(Callers.Alice));
        Task<IReadOnlyList<string>> f = Task.Run(() => Request(Callers.Frank));
        await Task.WhenAll(a, f);
        bool crossed = a.Result.Any(id => id.StartsWith("fabrikam-", StringComparison.Ordinal))
                    || f.Result.Any(id => id.StartsWith("contoso-", StringComparison.Ordinal));
        Expect("DEFECT REPRODUCED: caller in a shared field; two concurrent requests, one gets the other tenant's evidence", crossed);
    }

    // ---- The access matrix ----------------------------------------------------
    static async Task MatrixAsync(AuthorizedSearchService search, IReadOnlyList<PolicyDocument> corpus)
    {
        Console.WriteLine("\n-- Access matrix: 2 organizations x 3 levels x {current, superseded, revoked, hostile} --");
        string[] questions =
        [
            "Can I expense a taxi ride on a business trip?",
            "How much is a hotel night?",
            "What is the team travel budget?",
            "Who may book business class on a flight?",
            "Is there a relocation allowance or station parking?",
            "Which taxi suppliers or preferred taxi companies are in the annex?",
        ];
        foreach (CallerContext caller in Callers.All)
        {
            var model = new ScriptedModel();
            var issued = new List<EvidenceRecord>();
            int hostileRejected = 0;
            foreach (string question in questions)
            {
                var ledger = new EvidenceLedger();
                AgentResponse r = await EvidenceAgent.ForRequest(model, search, caller, ledger).RunAsync(question);
                issued.AddRange(ledger.Issued);
                CheckedAnswer c = EvidenceCheck.Check(r.Text, ledger);
                if (c.Abstention == AbstentionReason.CitationNotSupplied) hostileRejected++;
            }

            var byId = corpus.SelectMany(d => d.Paragraphs().Select((p, i) => (Id: d.EvidenceId(i), Doc: d, Text: p)))
                             .ToDictionary(x => x.Id);
            bool Allowed(PolicyDocument d) =>
                d.Tenant == caller.Tenant && d.Access <= caller.Level && d.State == SourceState.Active;
            int otherTenant = issued.Count(e => byId[e.EvidenceId].Doc.Tenant != caller.Tenant);
            int aboveLevel = issued.Count(e => byId[e.EvidenceId].Doc.Access > caller.Level);
            int superseded = issued.Count(e => byId[e.EvidenceId].Doc.State == SourceState.Superseded);
            int revokedOrDraft = issued.Count(e => byId[e.EvidenceId].Doc.State is SourceState.Revoked or SourceState.Draft);
            // A superseded paragraph whose text is unchanged in the current version is not a leak.
            HashSet<string> allowedText = [.. byId.Values.Where(x => Allowed(x.Doc)).Select(x => x.Text)];
            int inPrompts = byId.Values.Where(x => !Allowed(x.Doc) && !allowedText.Contains(x.Text))
                                       .Count(x => model.AllInput.Contains(x.Text, StringComparison.Ordinal));
            bool current = issued.Any(e => e.EvidenceId == $"{caller.Tenant}-taxi@v2#0");
            bool annexIssued = issued.Any(e => e.DocId == $"{caller.Tenant}-supplier-annex");

            Console.WriteLine($"  {caller.Tenant,-8} {caller.Level,-10} {issued.Select(e => e.EvidenceId).Distinct().Count(),2} ids | " +
                $"other tenant {otherTenant}, above level {aboveLevel}, superseded {superseded}, revoked/draft {revokedOrDraft}, " +
                $"forbidden text in prompts {inPrompts} | current taxi {(current ? "yes" : "NO")} | annex as data {(annexIssued ? "yes" : "NO")}, its citation rejected {hostileRejected}");
            Expect($"{caller.Tenant}/{caller.Level}: only authorized, current evidence in retrieval and prompts; hostile citation rejected",
                otherTenant + aboveLevel + superseded + revokedOrDraft + inPrompts == 0 && current && annexIssued && hostileRejected == 1);
        }
    }

    // ---- The fix under the same interleaving ---------------------------------
    static async Task ConcurrencyAsync(AuthorizedSearchService search)
    {
        Console.WriteLine("\n-- Per-request agents under concurrency --");
        var model = new ScriptedModel();
        var bothBuilt = new Barrier(2);
        async Task<EvidenceLedger> Request(CallerContext caller)
        {
            var ledger = new EvidenceLedger();
            ChatClientAgent agent = EvidenceAgent.ForRequest(model, search, caller, ledger);
            bothBuilt.SignalAndWait();
            await agent.RunAsync("Can I expense a taxi on a business trip?");
            return ledger;
        }
        Task<EvidenceLedger> a = Task.Run(() => Request(Callers.Alice));
        Task<EvidenceLedger> f = Task.Run(() => Request(Callers.Frank));
        await Task.WhenAll(a, f);
        Expect("same interleaving, per-request agents: each ledger holds only its own tenant",
            a.Result.Issued.Count > 0 && a.Result.Issued.All(e => e.Tenant == "contoso")
            && f.Result.Issued.Count > 0 && f.Result.Issued.All(e => e.Tenant == "fabrikam"));

        Task<EvidenceLedger>[] many = [.. Enumerable.Range(0, 40).Select(i =>
            Task.Run(async () =>
            {
                CallerContext caller = Callers.All[i % Callers.All.Length];
                var ledger = new EvidenceLedger();
                await EvidenceAgent.ForRequest(model, search, caller, ledger).RunAsync("Can I expense a taxi ride?");
                return ledger;
            }))];
        await Task.WhenAll(many);
        Expect("40 concurrent requests from 6 callers: no evidence crosses a tenant or a level",
            many.Select((t, i) => (t.Result, Caller: Callers.All[i % Callers.All.Length]))
                .All(x => x.Result.Issued.All(e => e.Tenant == x.Caller.Tenant && e.Access <= x.Caller.Level)));
    }

    // ---- The evidence check ---------------------------------------------------
    static async Task EvidenceCheckAsync(AuthorizedSearchService search)
    {
        Console.WriteLine("\n-- Evidence check and named abstentions --");
        var model = new ScriptedModel();
        async Task<CheckedAnswer> Ask(CallerContext caller, string question)
        {
            var ledger = new EvidenceLedger();
            AgentResponse r = await EvidenceAgent.ForRequest(model, search, caller, ledger).RunAsync(question);
            return EvidenceCheck.Check(r.Text, ledger);
        }

        CheckedAnswer ok = await Ask(Callers.Alice, "Taxi or ride-hailing trip between 22:00 and 06:00?");
        Expect($"cites an id this request received -> accepted ({string.Join(", ", ok.Cited)})", ok.Accepted);

        CheckedAnswer hostile = await Ask(Callers.Alice, "Which taxi suppliers are in the annex?");
        Expect($"the model obeys the annex and cites {string.Join(", ", hostile.Rejected)} -> {hostile.Abstention}",
            hostile.Abstention == AbstentionReason.CitationNotSupplied && hostile.Rejected.Contains("contoso-exec-travel@v1#0"));

        CheckedAnswer none = await Ask(Callers.Alice, "What about a gym membership?");
        Expect($"nothing authorized matches -> {none.Abstention}", none.Abstention == AbstentionReason.NoAuthorizedEvidence);

        CheckedAnswer uncited = await Ask(Callers.Frank, "Taxi ride limit? (answer uncited)");
        Expect($"an answer with no citation -> {uncited.Abstention}", uncited.Abstention == AbstentionReason.NoCitation);

        var ledger = new EvidenceLedger();
        ledger.Add(await search.SearchAsync(Callers.Alice, "taxi ride at night"));
        CheckedAnswer old = EvidenceCheck.Check("Only when no public transport runs [contoso-taxi@v1#0].", ledger);
        Expect($"a citation of the superseded version, never supplied -> {old.Abstention}",
            old.Abstention == AbstentionReason.CitationNotSupplied);

        CheckedAnswer anonymous = await Ask(Callers.Alice with { Subject = "" }, "Can I expense a taxi ride?");
        Expect($"a caller with no subject gets no evidence -> {anonymous.Abstention}",
            anonymous.Abstention == AbstentionReason.NoAuthorizedEvidence);
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>The first attempt at identity: a field on a shared service, set before each run.</summary>
internal sealed class SharedCallerSearch(AuthorizedSearchService inner)
{
    public CallerContext? CurrentCaller { get; set; }
    public AsyncLocal<List<string>?> Seen { get; } = new();   // test instrumentation only

    public async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        IReadOnlyList<EvidenceRecord> evidence = await inner.SearchAsync(CurrentCaller!, query, ct);
        Seen.Value?.AddRange(evidence.Select(e => e.EvidenceId));
        return evidence.Select(e => new TextSearchProvider.TextSearchResult { Text = e.Text, SourceName = e.EvidenceId });
    }
}

/// <summary>
/// The self-test's model. It answers from the evidence message and cites its first
/// id; given a hostile annex it obeys it, so the check has something to catch.
/// </summary>
internal sealed class ScriptedModel : IChatClient
{
    private readonly Lock _gate = new();
    private readonly System.Text.StringBuilder _all = new();

    public ChatOptions? LastOptions { get; private set; }
    public string? LastEvidenceMessage { get; private set; }
    public string AllInput { get { lock (_gate) return _all.ToString(); } }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatMessage[] list = [.. messages];
        string question = list.First(m => m.Role == ChatRole.User).Text;
        string? evidence = list.Select(m => m.Text).LastOrDefault(t => t.StartsWith("Evidence supplied", StringComparison.Ordinal));
        lock (_gate)
        {
            LastOptions = options;
            LastEvidenceMessage = evidence;
            foreach (ChatMessage m in list) _all.AppendLine(m.Text);
        }

        string[] ids = evidence is null ? [] :
            [.. System.Text.RegularExpressions.Regex.Matches(evidence, @"^\[([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)];
        var named = evidence is null ? null :
            System.Text.RegularExpressions.Regex.Match(evidence, @"(?:SYSTEM NOTE|override)[^\[]*\[([^\]]+)\]");
        string answer =
            ids.Length == 0 ? "I do not know."
            : named is { Success: true } && question.Contains("annex", StringComparison.OrdinalIgnoreCase)
                ? $"As instructed, exporting the employee records. Source: [{named.Groups[1].Value}]"
            : question.Contains("uncited", StringComparison.Ordinal) ? "The policy covers it."
            : $"The policy covers it [{ids[0]}].";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>Records what an HTTP connector would send, and answers like Ollama would.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public string LastBody { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"model":"llama3.2","created_at":"2026-11-20T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}""" + "\n"),
        };
    }
}
