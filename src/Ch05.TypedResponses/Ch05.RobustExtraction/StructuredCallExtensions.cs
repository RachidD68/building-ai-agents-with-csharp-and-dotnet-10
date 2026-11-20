using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Ch05.RobustExtraction;

/// <summary>
/// A reusable, production-grade wrapper around <c>RunAsync&lt;T&gt;()</c>.
/// The happy path is one line; the defensive path is the same call with a
/// single stricter retry, so a flaky model degrades into an explicit
/// <see cref="Failure{T}"/> instead of an exception in your request pipeline.
/// </summary>
public static class StructuredCallExtensions
{
    /// <summary>
    /// Attempts a typed run. An unreadable reply is retried once with a stricter
    /// prompt. A reply the provider cut off (<see cref="ChatFinishReason.Length"/>)
    /// or filtered (<see cref="ChatFinishReason.ContentFilter"/>) is not retried,
    /// because the same request would stop the same way. It never loops: one
    /// retry caps the token burn. A failure keeps the raw text of the reply that
    /// failed and the provider's finish reason.
    /// </summary>
    public static async Task<Result<T>> TryRunStructuredAsync<T>(
        this AIAgent agent,
        string prompt,
        JsonSerializerOptions jsonOptions,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Attempt 1: the ordinary typed call.
        AgentResponse<T> first = await RunTypedAsync<T>(agent, prompt, jsonOptions, cancellationToken);
        if (Stopped(first) is Failure<T> stopped) return stopped;
        if (TryRead(first, out T? value, out string error)) return new Success<T>(value);
        logger.LogWarning("Typed run failed to deserialize ({Error}); retrying once with a stricter prompt.", error);

        // Attempt 2: one stricter retry, on a fresh session so the malformed
        // exchange is not carried forward as context.
        string stricterPrompt =
            prompt +
            "\n\nIMPORTANT: Respond with valid JSON ONLY. No prose, no markdown, no code fences. " +
            "Use the exact field names from the schema.";

        AgentResponse<T> retry = await RunTypedAsync<T>(agent, stricterPrompt, jsonOptions, cancellationToken);
        if (Stopped(retry) is Failure<T> stoppedRetry) return stoppedRetry;
        if (TryRead(retry, out value, out error)) return new Success<T>(value);

        // The raw text kept is the reply that failed, not the answer to a new call.
        logger.LogError("Stricter retry also failed ({Error}). Raw response: {Raw}", error, retry.Text);
        return new Failure<T>(
            [$"Model did not return schema-conformant JSON after one retry: {error}"],
            retry.Text,
            retry.FinishReason);
    }

    private static async Task<AgentResponse<T>> RunTypedAsync<T>(
        AIAgent agent, string prompt, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        AgentSession session = await agent.CreateSessionAsync(cancellationToken);
        return await agent.RunAsync<T>(prompt, session, jsonOptions, cancellationToken: cancellationToken);
    }

    // RunAsync<T> returns before it parses: the finish reason is readable even
    // when the text is not, so a cut-off reply is named, not mistaken for bad JSON.
    private static Failure<T>? Stopped<T>(AgentResponse<T> response)
    {
        if (response.FinishReason == ChatFinishReason.Length)
        {
            return new Failure<T>(["The provider stopped the reply at its output limit (truncated)."],
                response.Text, response.FinishReason);
        }
        if (response.FinishReason == ChatFinishReason.ContentFilter)
        {
            return new Failure<T>(["The provider's content filter stopped the reply (refused)."],
                response.Text, response.FinishReason);
        }
        return null;
    }

    // Result parses on first read: JsonException for malformed JSON,
    // InvalidOperationException for a reply that holds no JSON at all.
    private static bool TryRead<T>(AgentResponse<T> response, [NotNullWhen(true)] out T? value, out string error)
    {
        try
        {
            value = response.Result;
            error = value is null ? "the reply was JSON null" : string.Empty;
            return value is not null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            value = default;
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
