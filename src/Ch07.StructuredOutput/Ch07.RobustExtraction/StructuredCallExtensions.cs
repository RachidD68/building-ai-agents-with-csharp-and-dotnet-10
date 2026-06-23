using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Ch07.RobustExtraction;

/// <summary>
/// A reusable, production-grade wrapper around <c>RunAsync&lt;T&gt;()</c>.
/// The happy path is one line; the defensive path is the same call with a
/// single stricter retry and an untyped fallback, so a flaky model degrades
/// gracefully instead of throwing into your request pipeline.
/// </summary>
public static class StructuredCallExtensions
{
    /// <summary>
    /// Attempts a typed run. On <see cref="JsonException"/> it retries once with
    /// a stricter prompt; on a second failure it falls back to an untyped run,
    /// logs the raw response, and returns a <see cref="Failure{T}"/>. It never
    /// loops — a hard cap of one retry keeps a bad prompt from burning tokens.
    /// </summary>
    public static async Task<Result<T>> TryRunStructuredAsync<T>(
        this ChatClientAgent agent,
        string prompt,
        JsonSerializerOptions jsonOptions,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Attempt 1: the ordinary typed call.
        try
        {
            AgentSession session = await agent.CreateSessionAsync(cancellationToken);
            AgentResponse<T> response = await agent.RunAsync<T>(
                prompt, session, jsonOptions, cancellationToken: cancellationToken);
            return new Success<T>(response.Result);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Typed run failed to deserialize; retrying once with a stricter prompt.");
        }

        // Attempt 2: one stricter retry. A fresh session avoids carrying the
        // malformed exchange forward as context.
        string stricterPrompt =
            prompt +
            "\n\nIMPORTANT: Respond with valid JSON ONLY. No prose, no markdown, no code fences. " +
            "Every field is required. Use the exact field names from the schema.";

        try
        {
            AgentSession retrySession = await agent.CreateSessionAsync(cancellationToken);
            AgentResponse<T> retry = await agent.RunAsync<T>(
                stricterPrompt, retrySession, jsonOptions, cancellationToken: cancellationToken);
            return new Success<T>(retry.Result);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Stricter retry also failed to deserialize; falling back to untyped run.");
        }

        // Fallback: untyped run so we can capture and log the raw text for review.
        AgentSession rawSession = await agent.CreateSessionAsync(cancellationToken);
        AgentResponse raw = await agent.RunAsync(
            stricterPrompt, rawSession, cancellationToken: cancellationToken);

        logger.LogError("Structured extraction failed after one retry. Raw response: {Raw}", raw.Text);

        return new Failure<T>(
            ["Model did not return schema-conformant JSON after one retry."],
            raw.Text);
    }
}
