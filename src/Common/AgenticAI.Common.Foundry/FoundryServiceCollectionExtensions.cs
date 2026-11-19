using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgenticAI.Common.Configuration;

namespace AgenticAI.Common.Extensions;

/// <summary>Registration for a Microsoft Foundry agent (preview packages).</summary>
public static class FoundryServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Microsoft Foundry-backed <see cref="AIAgent"/> in the DI
    /// container. The agent is constructed from an <see cref="AIProjectClient"/>
    /// via the <c>AsAIAgent</c> extension method (Microsoft.Agents.AI.Foundry).
    /// Foundry returns an <see cref="AIAgent"/> directly, not an IChatClient, so
    /// it has its own registration method instead of an arm in AddLlmChatClient.
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
        LlmConfigCheck.RunIfRequested(configuration);   // dotnet run -- --config-check

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
}
