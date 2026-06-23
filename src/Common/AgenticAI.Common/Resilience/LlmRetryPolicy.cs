using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace AgenticAI.Common.Resilience;

/// <summary>
/// Standard retry policy for LLM API calls.
/// Implements exponential backoff for rate limits (429) and transient failures (5xx).
/// </summary>
public static class LlmRetryPolicy
{
    /// <summary>
    /// Adds a resilient HTTP client with LLM-optimized retry policies.
    /// </summary>
    public static IHttpClientBuilder AddLlmResilienceHandler(this IHttpClientBuilder builder)
    {
        builder.AddStandardResilienceHandler(options =>
        {
            // Retry up to 3 times with exponential backoff
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromSeconds(2);

            // Increase total timeout for LLM calls (they can be slow)
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);

            // Increase attempt timeout for individual calls
            options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(1);
        });

        return builder;
    }
}
