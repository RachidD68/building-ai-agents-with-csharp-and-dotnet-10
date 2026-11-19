using Polly;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using AgenticAI.Common.Configuration;
using AgenticAI.Common.Providers;

namespace AgenticAI.Common.Extensions;

/// <summary>
/// Extension methods for registering LLM services in the DI container.
/// This package is GA-only: it references no prerelease package, directly or
/// transitively. Preview and vendor-specific wiring lives in the optional
/// packages AgenticAI.Common.Azure, AgenticAI.Common.Foundry and
/// AgenticAI.Common.Anthropic.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IChatClient"/> based on the configured provider
    /// in the "LlmProvider" configuration section.
    ///
    /// Every provider's HTTP traffic flows through a named HttpClient wrapped in
    /// <c>AddStandardResilienceHandler</c> — transient-fault retries, a circuit
    /// breaker, and timeouts re-tuned for LLM latency.
    ///
    /// Built in: <c>openai</c> and <c>ollama</c>. Another arm plugs in through
    /// <see cref="ILlmChatClientProvider"/>: reference AgenticAI.Common.Azure and
    /// call <c>AddAzureOpenAIProvider()</c> for <c>azure</c>. The order of the two
    /// calls does not matter. Foundry and Anthropic return an AIAgent, not an
    /// <see cref="IChatClient"/>; they have their own <c>AddFoundryAgent</c> and
    /// <c>AddAnthropicAgent</c> in their packages.
    /// </summary>
    public static IServiceCollection AddLlmChatClient(this IServiceCollection services, IConfiguration configuration,
        TimeSpan? attemptTimeout = null)
    {
        LlmConfigCheck.RunIfRequested(configuration);   // dotnet run -- --config-check

        LlmSettings settings = new();
        configuration.GetSection("LlmProvider").Bind(settings);

        services.AddLlmChatClientProvider(new OpenAIChatClientProvider());
        services.AddLlmChatClientProvider(new OllamaChatClientProvider());

        // Fail at registration, before any model call, when the selected arm is
        // already registered and a required setting is missing. An arm that is
        // registered later is checked when the IChatClient is first resolved.
        ILlmChatClientProvider? known = FindProvider(
            services.Select(d => d.IsKeyedService ? null : d.ImplementationInstance).OfType<ILlmChatClientProvider>(),
            settings.Provider);
        if (known is not null)
        {
            ThrowIfMissing(known, settings);
        }

        // Every provider call rides one named HttpClient wrapped in the
        // standard resilience handler (retry on transient 408/429/5xx, circuit
        // breaker, timeouts). The default envelope assumes sub-second APIs, so
        // it is re-tuned for LLM latency: a local model can legitimately take
        // minutes on a long prompt, and a 10-second attempt timeout would turn
        // every slow completion into a retry storm. (Streaming responses return
        // at the headers, so the attempt timeout does not cut off a stream
        // mid-token.)
        services.AddHttpClient("llm").AddLlmResilienceHandler(attemptTimeout);

        // IHttpClientFactory logs every request/response at Information, which
        // would drown each demo's console output. Keep the pipeline, quiet the
        // narration; real problems still surface at Warning and above.
        services.AddLogging(logging =>
        {
            logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            logging.AddFilter("Polly", LogLevel.Warning);
        });

        // Construction is deferred into the container so the client can be
        // built over IHttpClientFactory's pipeline instead of a raw socket.
        services.AddSingleton<IChatClient>(sp =>
        {
            ILlmChatClientProvider provider =
                FindProvider(sp.GetServices<ILlmChatClientProvider>(), settings.Provider)
                ?? throw UnknownProvider(settings.Provider);
            ThrowIfMissing(provider, settings);

            HttpClient http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm");
            return provider.CreateChatClient(http, settings);
        });

        return services;
    }

    /// <summary>
    /// Adds an arm to the provider switch of
    /// <see cref="AddLlmChatClient(IServiceCollection, IConfiguration, TimeSpan?)"/>.
    /// Registering the same provider type twice is a no-op. When two providers
    /// claim the same name, the one registered last wins.
    /// </summary>
    public static IServiceCollection AddLlmChatClientProvider(this IServiceCollection services,
        ILlmChatClientProvider provider)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton(provider));
        return services;
    }

    private static ILlmChatClientProvider? FindProvider(IEnumerable<ILlmChatClientProvider> providers, string name) =>
        providers.LastOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // Fail with the name of the missing setting instead of an opaque
    // "Value cannot be an empty string" from the SDK.
    private static void ThrowIfMissing(ILlmChatClientProvider provider, LlmSettings settings)
    {
        string? missing = provider.FindMissingSetting(settings);
        if (missing is not null)
        {
            throw new InvalidOperationException(
                $"Provider '{settings.Provider}' needs {missing}. Set it with " +
                $"dotnet user-secrets set \"{missing}\" \"<value>\" (from the project folder) " +
                $"or the environment variable {missing.Replace(":", "__")}. " +
                "Run with --config-check to see which settings were found.");
        }
    }

    private static InvalidOperationException UnknownProvider(string name) => name.ToLowerInvariant() switch
    {
        "azure" => new InvalidOperationException(
            "Provider 'azure' (Azure OpenAI) lives in the optional package AgenticAI.Common.Azure, " +
            "because Azure.AI.OpenAI is a prerelease package. Add a ProjectReference to " +
            "src/Common/AgenticAI.Common.Azure and call services.AddAzureOpenAIProvider(), " +
            "or set LlmProvider:Provider to openai or ollama."),

        "foundry" => new InvalidOperationException(
            "Provider 'foundry' returns an AIAgent, not an IChatClient. Reference " +
            "AgenticAI.Common.Foundry and call AddFoundryAgent(...) instead of AddLlmChatClient(...)."),

        "anthropic" => new InvalidOperationException(
            "Provider 'anthropic' returns an AIAgent, not an IChatClient. Reference " +
            "AgenticAI.Common.Anthropic and call AddAnthropicAgent(...) instead of AddLlmChatClient(...)."),

        _ => new InvalidOperationException(
            $"Unknown LLM provider: '{name}'. Valid values: openai, ollama " +
            "(azure with AgenticAI.Common.Azure; foundry and anthropic through their own Add...Agent methods)."),
    };

    /// <summary>
    /// Attaches the standard resilience pipeline (transient-fault retries with
    /// exponential backoff + jitter, circuit breaker, timeouts) to the HttpClient
    /// that backs the LLM provider client — with the timeout envelope re-tuned
    /// for LLM latency. The defaults assume sub-second APIs; a local model can
    /// legitimately generate for minutes, and a non-streamed completion does not
    /// return headers until generation finishes.
    /// </summary>
    /// <param name="attemptTimeout">
    /// Per-attempt timeout. Defaults to two minutes, which suits a single chat
    /// call. Compositions that send a large tool schema and then loop -- the
    /// agent harness is the clearest case -- can legitimately exceed that on
    /// local hardware, so they widen it here rather than letting Polly cancel a
    /// call that was going to succeed.
    /// </param>
    public static IHttpClientBuilder AddLlmResilienceHandler(this IHttpClientBuilder builder,
        TimeSpan? attemptTimeout = null)
    {
        TimeSpan attempt = attemptTimeout ?? TimeSpan.FromMinutes(2);
        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;                      // the standard defaults,
            options.Retry.BackoffType = DelayBackoffType.Exponential; // spelled out so the
            options.Retry.UseJitter = true;                           // knobs are visible
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.AttemptTimeout.Timeout = attempt;                        // LLM calls are slow
            options.TotalRequestTimeout.Timeout = attempt * 2.5;
            // Options validation requires SamplingDuration >= 2x the attempt timeout.
            options.CircuitBreaker.SamplingDuration = attempt * 2;
        });
        return builder;
    }

    /// <summary>
    /// Reads a required configuration value or throws with a helpful message.
    /// </summary>
    public static string GetRequiredValue(this IConfiguration configuration, string key)
    {
        return configuration[key]
            ?? throw new InvalidOperationException(
                $"Missing configuration '{key}'. " +
                $"Set '{key}' in appsettings.json (or as an environment variable).");
    }
}
