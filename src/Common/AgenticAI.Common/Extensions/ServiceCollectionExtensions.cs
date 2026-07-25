using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Polly;
using Azure.AI.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgenticAI.Common.Configuration;

namespace AgenticAI.Common.Extensions;

/// <summary>
/// Extension methods for registering LLM services in the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IChatClient"/> based on the configured provider
    /// in the "LlmProvider" configuration section.
    ///
    /// Every provider's HTTP traffic flows through a named HttpClient wrapped in
    /// <c>AddStandardResilienceHandler</c> — transient-fault retries, a circuit
    /// breaker, and timeouts re-tuned for LLM latency (see Chapter 14).
    ///
    /// Supports the four chat-completion providers: azure, openai, github, ollama.
    /// For the Microsoft Foundry path (provider="foundry"), use
    /// <see cref="AddFoundryAgent(IServiceCollection, IConfiguration, string, string)"/>
    /// instead — Foundry returns an <see cref="AIAgent"/> directly, not an
    /// <see cref="IChatClient"/>.
    /// </summary>
    public static IServiceCollection AddLlmChatClient(this IServiceCollection services, IConfiguration configuration)
    {
        LlmSettings settings = new();
        configuration.GetSection("LlmProvider").Bind(settings);

        // Every provider call rides one named HttpClient wrapped in the
        // standard resilience handler (retry on transient 408/429/5xx, circuit
        // breaker, timeouts). The default envelope assumes sub-second APIs, so
        // it is re-tuned for LLM latency: a local model can legitimately take
        // minutes on a long prompt, and a 10-second attempt timeout would turn
        // every slow completion into a retry storm. (Streaming responses return
        // at the headers, so the attempt timeout does not cut off a stream
        // mid-token.)
        services.AddHttpClient("llm").AddLlmResilienceHandler();

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
            HttpClient http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("llm");

            return settings.Provider.ToLowerInvariant() switch
            {
                "azure" => new AzureOpenAIClient(
                        new Uri(settings.AzureOpenAI.Endpoint),
                        // Pro Tip: unscoped DefaultAzureCredential probes the whole chain
                        // (env, workload/managed identity, Visual Studio, Azure CLI...), which is
                        // great for local dev but adds cold-start latency in production. There,
                        // scope it: new DefaultAzureCredential(new DefaultAzureCredentialOptions
                        // { ExcludeVisualStudioCredential = true, ExcludeAzureCliCredential = true })
                        // — or use ManagedIdentityCredential directly.
                        new DefaultAzureCredential(),
                        new AzureOpenAIClientOptions
                        {
                            Transport = new HttpClientPipelineTransport(http)
                        })
                    .GetChatClient(settings.AzureOpenAI.Model)
                    .AsIChatClient(),

                "openai" => new OpenAI.OpenAIClient(
                        new ApiKeyCredential(settings.OpenAI.ApiKey),
                        new OpenAI.OpenAIClientOptions
                        {
                            Transport = new HttpClientPipelineTransport(http)
                        })
                    .GetChatClient(settings.OpenAI.Model)
                    .AsIChatClient(),

                "github" => new OpenAI.OpenAIClient(
                        new ApiKeyCredential(settings.GitHub.Token),
                        new OpenAI.OpenAIClientOptions
                        {
                            Endpoint = new Uri(settings.GitHub.Endpoint),
                            Transport = new HttpClientPipelineTransport(http)
                        })
                    .GetChatClient(settings.GitHub.Model)
                    .AsIChatClient(),

                "ollama" => CreateOllamaClient(http, settings),

                "foundry" => throw new InvalidOperationException(
                    "Provider 'foundry' returns an AIAgent, not an IChatClient. " +
                    "Call AddFoundryAgent(...) instead of AddLlmChatClient(...). " +
                    "See Chapter 4 for the Foundry quick-start."),

                _ => throw new InvalidOperationException(
                    $"Unknown LLM provider: '{settings.Provider}'. " +
                    "Valid values: azure, openai, github, ollama, foundry.")
            };
        });

        return services;
    }

    private static IChatClient CreateOllamaClient(HttpClient http, LlmSettings settings)
    {
        // OllamaSharp takes the endpoint from the HttpClient's BaseAddress;
        // CreateClient returns a fresh instance, so setting it here is safe.
        http.BaseAddress = new Uri(settings.Ollama.Endpoint);
        return new OllamaSharp.OllamaApiClient(http, settings.Ollama.Model);
    }

    /// <summary>
    /// Attaches the standard resilience pipeline (transient-fault retries with
    /// exponential backoff + jitter, circuit breaker, timeouts) to the HttpClient
    /// that backs the LLM provider client — with the timeout envelope re-tuned
    /// for LLM latency. The defaults assume sub-second APIs; a local model can
    /// legitimately generate for minutes, and a non-streamed completion does not
    /// return headers until generation finishes.
    /// </summary>
    public static IHttpClientBuilder AddLlmResilienceHandler(this IHttpClientBuilder builder)
    {
        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;                      // the standard defaults,
            options.Retry.BackoffType = DelayBackoffType.Exponential; // spelled out so the
            options.Retry.UseJitter = true;                           // knobs are visible
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);       // LLM calls are slow
            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            // Options validation requires SamplingDuration >= 2x the attempt timeout.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(4);
        });
        return builder;
    }

    /// <summary>
    /// Registers a Microsoft Foundry-backed <see cref="AIAgent"/> in the DI
    /// container. Foundry is Microsoft's recommended path for new MAF projects;
    /// the agent is constructed from an <see cref="AIProjectClient"/> via the
    /// <c>AsAIAgent</c> extension method (Microsoft.Agents.AI.Foundry).
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">Configuration with an "LlmProvider:Foundry" section.</param>
    /// <param name="instructions">The agent's system prompt.</param>
    /// <param name="name">The agent's display name.</param>
    public static IServiceCollection AddFoundryAgent(
        this IServiceCollection services,
        IConfiguration configuration,
        string instructions,
        string name)
    {
        LlmSettings settings = new();
        configuration.GetSection("LlmProvider").Bind(settings);

        if (string.IsNullOrWhiteSpace(settings.Foundry.ProjectEndpoint))
        {
            throw new InvalidOperationException(
                "LlmProvider:Foundry:ProjectEndpoint is required. " +
                "Set it in appsettings.json (or as an environment variable) — e.g. " +
                "\"https://my-foundry.services.ai.azure.com/api/projects/my-project\".");
        }

        AIAgent agent = new AIProjectClient(
                new Uri(settings.Foundry.ProjectEndpoint),
                // Pro Tip: unscoped DefaultAzureCredential probes the whole chain
                // (env, workload/managed identity, Visual Studio, Azure CLI...), which is
                // great for local dev but adds cold-start latency in production. There,
                // scope it: new DefaultAzureCredential(new DefaultAzureCredentialOptions
                // { ExcludeVisualStudioCredential = true, ExcludeAzureCliCredential = true })
                // — or use ManagedIdentityCredential directly.
                new DefaultAzureCredential())
            .AsAIAgent(
                model: settings.Foundry.Model,
                instructions: instructions,
                name: name);

        services.AddSingleton(agent);
        return services;
    }

    /// <summary>
    /// Registers an Anthropic (Claude) <see cref="AIAgent"/> in the DI container.
    /// Like Foundry, the Anthropic provider returns an <see cref="AIAgent"/> directly
    /// (via <c>IAnthropicClient.AsAIAgent(...)</c> from Microsoft.Agents.AI.Anthropic),
    /// not an <see cref="IChatClient"/> — so it has its own registration method rather
    /// than an arm in <see cref="AddLlmChatClient(IServiceCollection, IConfiguration)"/>.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">Configuration with an "LlmProvider:Anthropic" section.</param>
    /// <param name="instructions">The agent's system prompt.</param>
    /// <param name="name">The agent's display name.</param>
    public static IServiceCollection AddAnthropicAgent(
        this IServiceCollection services,
        IConfiguration configuration,
        string instructions,
        string name)
    {
        LlmSettings settings = new();
        configuration.GetSection("LlmProvider").Bind(settings);

        if (string.IsNullOrWhiteSpace(settings.Anthropic.ApiKey))
        {
            throw new InvalidOperationException(
                "LlmProvider:Anthropic:ApiKey is required. " +
                "Set it via user secrets (or the ANTHROPIC_API_KEY environment variable) — " +
                "create a key in the Anthropic Console.");
        }

        AnthropicClient client = new() { ApiKey = settings.Anthropic.ApiKey };

        AIAgent agent = client.AsAIAgent(
            model: settings.Anthropic.Model,
            instructions: instructions,
            name: name);

        services.AddSingleton(agent);
        return services;
    }

    /// <summary>
    /// Reads a required configuration value or throws with a helpful message.
    /// </summary>
    public static string GetRequiredValue(this IConfiguration configuration,string key)
    {
        return configuration[key]
            ?? throw new InvalidOperationException(
                $"Missing configuration '{key}'. " +
                $"Set '{key}' in appsettings.json (or as an environment variable).");
    }
}
