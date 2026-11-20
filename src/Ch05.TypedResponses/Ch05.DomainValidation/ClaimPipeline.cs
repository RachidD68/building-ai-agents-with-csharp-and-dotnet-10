using System.Diagnostics;
using Ch05.RobustExtraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch05.DomainValidation;

/// <summary>
/// Shape, then meaning. TryRunStructuredAsync gives a typed draft or a named
/// failure; the validator turns the draft into Valid, Invalid or Missing.
/// Every path ends in an explicit <see cref="ClaimOutcome"/>.
/// </summary>
public static class ClaimPipeline
{
    public const string Instructions = """
        You extract expense claims into JSON.
        Amounts are JSON numbers with a dot as the decimal separator, e.g. 64.80.
        The currency is the ISO 4217 code in capitals, e.g. EUR.
        Use null for any field the text does not state. Never guess an amount.
        Set confidence to a number from 0.0 to 1.0.
        """;

    public static string Prompt(string claimText) =>
        $"Extract the expense claim from this text:\n\n{claimText}";

    public static async Task<ClaimOutcome> ExtractAsync(
        AIAgent agent, string claimText, ClaimRules rules, ILogger logger, CancellationToken cancellationToken = default)
    {
        Result<ClaimDraft> result = await agent.TryRunStructuredAsync<ClaimDraft>(
            Prompt(claimText), ClaimJson.Options, logger, cancellationToken);

        return result switch
        {
            Success<ClaimDraft> ok => ClaimValidator.Validate(ok.Value, claimText, rules),
            Failure<ClaimDraft> f when f.FinishReason == ChatFinishReason.Length => new Truncated(f.RawResponse),
            Failure<ClaimDraft> f when f.FinishReason == ChatFinishReason.ContentFilter => new Refused(f.RawResponse),
            Failure<ClaimDraft> f => new Unreadable(f.RawResponse, string.Join("; ", f.Messages)),
            _ => throw new UnreachableException()
        };
    }
}
