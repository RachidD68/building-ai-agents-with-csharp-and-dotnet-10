using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgenticAI.Common.Configuration;

namespace AgenticAI.Common.Extensions;

/// <summary>Registration for an Anthropic (Claude) agent.</summary>
public static class AnthropicServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Anthropic (Claude) <see cref="AIAgent"/> in the DI container.
    /// Like Foundry, the Anthropic provider returns an <see cref="AIAgent"/> directly
    /// (via <c>IAnthropicClient.AsAIAgent(...)</c> from Microsoft.Agents.AI.Anthropic),
    /// not an IChatClient — so it has its own registration method rather than an
    /// arm in AddLlmChatClient.
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
        LlmConfigCheck.RunIfRequested(configuration);   // dotnet run -- --config-check

        LlmSettings settings = new();
        configuration.GetSection("LlmProvider").Bind(settings);

        // LlmProvider:Anthropic:ApiKey wins; the conventional ANTHROPIC_API_KEY
        // environment variable is the fallback.
        string apiKey = !string.IsNullOrWhiteSpace(settings.Anthropic.ApiKey)
            ? settings.Anthropic.ApiKey
            : configuration["ANTHROPIC_API_KEY"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "LlmProvider:Anthropic:ApiKey is required. Set it with " +
                "dotnet user-secrets set \"LlmProvider:Anthropic:ApiKey\" \"<key>\" " +
                "or the ANTHROPIC_API_KEY environment variable. " +
                "Run with --config-check to see which settings were found.");
        }

        AnthropicClient client = new() { ApiKey = apiKey };

        AIAgent agent = client.AsAIAgent(
            model: settings.Anthropic.Model,
            instructions: instructions,
            name: name);

        services.AddSingleton(agent);
        return services;
    }
}
