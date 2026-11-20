using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Contoso.AgentFakeModel;

/// <summary>
/// The v0 desk: one policy question in, one streamed answer out, with its
/// terminal status. Single-turn: no session, so no question sees another's
/// history (the owned conversation arrives in v1). No tools, no effects.
/// </summary>
public sealed class PolicyDesk(IChatClient model)
{
    private readonly ChatClientAgent _agent = ContosoPolicy.Create(model);

    public const int MaxQuestionLength = 2000;

    public string Name => _agent.Name!;

    public Task<RunOutcome> AskAsync(string question, CancellationToken cancellationToken, Action<string>? onText = null)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > MaxQuestionLength)
            throw new ArgumentException($"A question is 1 to {MaxQuestionLength} characters.", nameof(question));
        return AgentRuns.CollectAsync(
            _agent.RunStreamingAsync(question, cancellationToken: cancellationToken), cancellationToken, onText);
    }
}
