using System.Text.Json;
using Ch20.CriticAgent.Models;
using Ch20.InputSanitization.Sanitizers;
using Microsoft.Agents.AI;

namespace Ch20.CriticAgent;

public enum GateDecision { Publish, Reject, Escalate }

public sealed record GateResult(GateDecision Decision, string Reason);

/// <summary>
/// The critic is one scored signal, compared with a deterministic detector.
/// The gate fails closed when the critic gives no usable verdict, and sends
/// disagreements to a person instead of letting either side win silently.
/// It judges text. It has no idea whether an action described in that text is
/// allowed; that question belongs to the policy at the effect.
/// </summary>
public static class CriticGate
{
    public const int PassScore = 7;

    public static GateResult Decide(ValidationResult? critic, bool detectorFoundPii)
    {
        if (detectorFoundPii) return new(GateDecision.Reject, "pii_detector");          // deterministic wins
        if (critic is null) return new(GateDecision.Reject, "critic_unavailable");       // fail closed
        if (critic.Score is < 1 or > 10) return new(GateDecision.Reject, "critic_out_of_range");
        if (critic.PiiDetected) return new(GateDecision.Escalate, "critic_sees_pii_detector_does_not");
        if (critic.Passed != critic.Score >= PassScore) return new(GateDecision.Escalate, "critic_inconsistent");
        return critic.Score >= PassScore
            ? new(GateDecision.Publish, "critic_score")
            : new(GateDecision.Reject, "critic_score");
    }
}

/// <summary>Writer, then critic (structured output), then the gate.</summary>
public sealed class CriticPipeline(AIAgent writer, AIAgent critic, JsonSerializerOptions json)
{
    public sealed record Review(string Content, ValidationResult? Critic, bool DetectorFoundPii, GateResult Gate);

    public async Task<Review> ReviewAsync(string prompt, CancellationToken ct = default)
    {
        string content = (await writer.RunAsync(prompt, cancellationToken: ct)).Text;

        string criticPrompt = $"""
            Evaluate the following technical response for quality.

            Original question: {prompt}

            Response to evaluate:
            {content}
            """;

        ValidationResult? verdict;
        try
        {
            verdict = (await critic.RunAsync<ValidationResult>(criticPrompt, null, json, cancellationToken: ct)).Result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            verdict = null;     // no verdict is not a pass
        }

        bool pii = PiiDetector.Scan(content).Count > 0;
        return new Review(content, verdict, pii, CriticGate.Decide(verdict, pii));
    }
}
