using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch10.FaqAgent;

/// <summary>
/// The custom alternative to <see cref="TextSearchProvider"/>: before each call
/// it retrieves chunks for this call's question and adds them as one message.
/// </summary>
/// <remarks>
/// The question comes from <see cref="MessageAIContextProvider.InvokingContext.RequestMessages"/>,
/// the caller's messages for this invocation. The provider has no mutable field,
/// so one instance can be a singleton shared by concurrent requests.
/// Reach for it when the built-in is not enough: your own evidence format,
/// reranking, or several retrieval sources.
/// </remarks>
public sealed class FaqContextProvider(FaqSearchService search) : MessageAIContextProvider
{
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        string? question = context.RequestMessages
            .LastOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrWhiteSpace(question)) return [];

        IReadOnlyList<FaqHit> hits = await search.RetrieveAsync(question, cancellationToken);
        if (hits.Count == 0) return [];

        return [new ChatMessage(ChatRole.User, FormatEvidence(hits))];
    }

    /// <summary>One block per hit, tagged with its source so the answer can cite it.</summary>
    public static string FormatEvidence(IEnumerable<FaqHit> hits) =>
        "Handbook excerpts for this question:\n\n" + string.Join("\n\n",
            hits.Select(h => $"[Source: {h.Source}]\n{h.Text}"));
}
