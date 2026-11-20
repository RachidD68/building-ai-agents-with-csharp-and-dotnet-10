using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace AgenticAI.Common.Extensions;

/// <summary>The Microsoft Foundry project handle, built from configuration (preview packages).</summary>
public static class FoundryProjects
{
    /// <summary>
    /// An <see cref="AIProjectClient"/> for LlmProvider:Foundry:ProjectEndpoint, or null when
    /// no endpoint is configured, so a caller can skip its Foundry path with a message.
    /// The credential is DefaultAzureCredential (az login locally, managed identity in Azure).
    /// </summary>
    public static AIProjectClient? TryCreate(IConfiguration configuration)
    {
        string? endpoint = configuration["LlmProvider:Foundry:ProjectEndpoint"];
        return string.IsNullOrWhiteSpace(endpoint)
            ? null
            : new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());
    }
}
