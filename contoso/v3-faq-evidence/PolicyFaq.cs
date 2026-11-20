using System.Globalization;
using Ch11.AuthorizedSearch;
using Ch11.ScopedCache;
using Ch11.VersionedIngestion;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace Contoso.FaqEvidence;

public sealed record FaqAnswer(
    string Text, string? Abstention, IReadOnlyList<string> Evidence, bool FromCache, IReadOnlyList<string> Warnings);

/// <summary>
/// The policy FAQ of the Contoso assistant. The ingestion store is the record of
/// which version is active; the vector index mirrors it and keeps every state, so
/// the authorized search's filter is what keeps drafts, superseded and other
/// tenants' chunks out. Every publish or withdrawal revokes the cache entries
/// built from that document. Answers are delivered only when their citations
/// are ids this request received; abstentions are never cached.
/// </summary>
public sealed class PolicyFaq
{
    public const string PromptVersion = "faq-evidence-1";
    private readonly IChatClient _chat;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly string _embeddingModel;
    private readonly string _modelId;
    private readonly AuthorizedSearchService _search;
    private readonly VectorStoreCollection<string, PolicyChunk> _index;

    public PolicyFaq(IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embedder, string embeddingModel,
        string modelId, VectorStoreCollection<string, PolicyChunk> index, IngestionStore store, ScopedCache cache,
        double minScore)
    {
        (_chat, _embedder, _embeddingModel, _modelId, _index, Store, Cache) =
            (chat, embedder, embeddingModel, modelId, index, store, cache);
        _search = new AuthorizedSearchService(embedder, index, top: 3, minScore);
    }

    public IngestionStore Store { get; }
    public ScopedCache Cache { get; }

    /// <summary>
    /// Ingests one source version; activates it when the source says it is in force and its
    /// effective date has come. The vector index mirrors the store in separate steps, so for a
    /// moment both versions are Active there; a production system flips both in one step, or
    /// filters on the store's active version.
    /// </summary>
    public async Task<ChangeNotice?> PublishAsync(PolicyDocument doc, DateOnly today, CancellationToken ct = default)
    {
        IngestResult found = Store.Discover(doc);
        if (found.Error is not null) return new ChangeNotice(doc.Tenant, doc.DocId, null, null, found.Error);
        Store.Validate(found.Id);
        await Store.IndexAsync(found.Id, _embedder, _embeddingModel, ct: ct);
        if (doc.State != SourceState.Active)
        {
            await _search.IndexAsync([doc with { State = SourceState.Draft }], ct);     // indexed, never searchable
            return null;
        }
        ChangeNotice notice = Store.Activate(found.Id, today);
        if (notice.Error == "not_yet_effective")
        {
            await _search.IndexAsync([doc with { State = SourceState.Draft }], ct);     // waits for its date
            return notice;
        }
        if (notice.Activated is int activated)
        {
            await _search.IndexAsync([doc], ct);
            if (notice.Retired is int retired) await SetStateAsync(doc.Tenant, doc.DocId, retired, SourceState.Superseded, ct);
            Cache.Revoke(doc.Tenant, doc.DocId);
        }
        return notice;
    }

    /// <summary>Deletes a document: tombstone, every chunk out of the index, every cache entry built from it.</summary>
    public async Task<ChangeNotice> WithdrawAsync(string tenant, string docId, DateTimeOffset now, CancellationToken ct = default)
    {
        ChangeNotice notice = Store.Delete(tenant, docId, now);
        List<string> keys = [];
        await foreach (PolicyChunk c in _index.GetAsync(c => c.Tenant == tenant && c.DocId == docId, 1000, cancellationToken: ct))
            keys.Add(c.EvidenceId);
        foreach (string key in keys) await _index.DeleteAsync(key, ct);
        Cache.Revoke(tenant, docId);
        return notice;
    }

    public async Task<FaqAnswer> AskAsync(CallerContext caller, string question, DateOnly? tripDate = null, CancellationToken ct = default)
    {
        CacheScope scope = CacheScope.Of(caller);
        string key = Cache.AnswerKey(scope, question,
            $"{IngestionStore.IngestionVersion}/index-{Store.CurrentIndex().Version}/{tripDate:yyyy-MM-dd}", _modelId, PromptVersion);
        CacheLookup cached = Cache.TryGet(key, Readable);
        if (cached.Hit) return new FaqAnswer(cached.Value!, null, [], true, Warnings(caller, cached.Value!, tripDate));

        long token = Cache.BeginCompute(caller.Tenant);                         // before retrieval
        var ledger = new EvidenceLedger();
        AgentResponse response = await EvidenceAgent.ForRequest(_chat, _search, caller, ledger).RunAsync(question, cancellationToken: ct);
        CheckedAnswer check = EvidenceCheck.Check(response.Text, ledger);
        string[] evidence = [.. ledger.Issued.Select(e => e.EvidenceId)];
        if (!check.Accepted)
        {
            string text = check.Abstention switch
            {
                AbstentionReason.NoAuthorizedEvidence => "I can't find a policy you have access to that answers this. Please ask the travel desk.",
                AbstentionReason.CitationNotSupplied => "I can't give a checked answer to this one. Please ask the travel desk.",
                _ => "I can't point to the policy behind an answer. Please ask the travel desk.",
            };
            return new FaqAnswer(text, check.Abstention, evidence, false, []);
        }
        Cache.TryPut(key, response.Text, scope, ledger.Issued.Select(Dependency.Of), token);
        return new FaqAnswer(response.Text, null, evidence, false, Warnings(caller, response.Text, tripDate));
    }

    /// <summary>Delivery-time check: the version must still be active and readable at this level.</summary>
    private bool Readable(CacheScope scope, Dependency d) =>
        scope.Tenant == d.Tenant && Store.Find(d.Tenant, d.DocId, d.Version) is { State: VersionState.Active } found
        && found.Access <= scope.Level;

    /// <summary>A question about a past date gets told when the cited version did not apply then.</summary>
    private List<string> Warnings(CallerContext caller, string answer, DateOnly? tripDate)
    {
        if (tripDate is not DateOnly day) return [];
        List<string> warnings = [];
        foreach (string id in System.Text.RegularExpressions.Regex.Matches(answer, @"\[([a-z0-9-]+)@v(\d+)#\d+\]")
                     .Select(m => $"{m.Groups[1].Value}@{m.Groups[2].Value}").Distinct())
        {
            string docId = id[..id.IndexOf('@')];
            int version = int.Parse(id[(id.IndexOf('@') + 1)..], CultureInfo.InvariantCulture);
            Applicable applicable = Store.ApplicableOn(caller.Tenant, docId, day);
            if (applicable.Version != version)
                warnings.Add(applicable.Version is int v
                    ? $"{docId}: on {day:yyyy-MM-dd} version {v} applied, not the cited version {version}"
                    : $"{docId}: no version was in force on {day:yyyy-MM-dd}");
        }
        return warnings;
    }

    private async Task SetStateAsync(string tenant, string docId, int version, SourceState state, CancellationToken ct)
    {
        List<PolicyChunk> chunks = [];
        await foreach (PolicyChunk c in _index.GetAsync(c => c.Tenant == tenant && c.DocId == docId && c.Version == version, 1000, cancellationToken: ct))
            chunks.Add(c);
        foreach (PolicyChunk c in chunks)
        {
            c.State = state.ToString();
            await _index.UpsertAsync(c, ct);
        }
    }
}
