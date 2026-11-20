using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch21.EvidenceEval;

/// <summary>
/// The five layers as MAF checks, one per name, so a LocalEvaluator reports each
/// layer on its own. A layer that does not apply passes with a reason that starts
/// with "n/a"; the report counts those apart, never as passes.
/// </summary>
public static class EvidenceChecks
{
    public static readonly string[] Names = ["fact.value", "evidence.support", "citation.issued", "abstention", "action.allowed"];

    public static LocalEvaluator Evaluator(CaseBook book) =>
        new([.. Enum.GetValues<Layer>().Select(layer => FunctionEvaluator.Create(Names[(int)layer],
            (EvalItem item) =>
            {
                LayerScore s = ScoreItem(book, item)[(int)layer];
                return new EvalCheckResult(s.Passed, s.Reason, Names[(int)layer]);
            }))]);

    public static IReadOnlyList<LayerScore> ScoreItem(CaseBook book, EvalItem item) =>
        EvidenceScorer.Score(book.For(item.Query), item.Response, ToolCalls(item));

    /// <summary>Every tool call the conversation carries, from the agent's own messages.</summary>
    public static IReadOnlyList<FunctionCallContent> ToolCalls(EvalItem item) =>
        [.. item.Conversation.Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents).OfType<FunctionCallContent>()];
}
