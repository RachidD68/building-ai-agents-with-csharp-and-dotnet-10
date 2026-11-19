using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using AgenticAI.Common.Configuration;
using AgenticAI.Common.Providers;

namespace AgenticAI.Common.Azure;

/// <summary>
/// The <c>azure</c> arm of AddLlmChatClient: an Azure OpenAI deployment over the
/// "llm" HttpClient. Register it with <c>services.AddAzureOpenAIProvider()</c>.
/// </summary>
public sealed class AzureOpenAIChatClientProvider : ILlmChatClientProvider
{
    public string Name => "azure";

    public string? FindMissingSetting(LlmSettings settings) =>
        string.IsNullOrWhiteSpace(settings.AzureOpenAI.Endpoint) ? "LlmProvider:AzureOpenAI:Endpoint" : null;

    public IChatClient CreateChatClient(HttpClient http, LlmSettings settings) =>
        CreateClient(http, settings.AzureOpenAI)
            .GetChatClient(settings.AzureOpenAI.Model)
            .AsIChatClient();

    /// <summary>
    /// Builds an <see cref="AzureOpenAIClient"/> from the AzureOpenAI settings.
    /// An API key, when configured, is used as-is; otherwise Microsoft Entra ID
    /// through DefaultAzureCredential (run <c>az login</c> once for local dev).
    /// </summary>
    /// <param name="http">
    /// The HttpClient to send through, normally the resilient "llm" client;
    /// <see langword="null"/> keeps the SDK's own transport and retries.
    /// </param>
    public static AzureOpenAIClient CreateClient(HttpClient? http, AzureOpenAISettings azure)
    {
        AzureOpenAIClientOptions options = new();
        if (http is not null)
        {
            options.Transport = new HttpClientPipelineTransport(http);
            // One retry owner: the resilience handler on the "llm" HttpClient.
            options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        }
        Uri endpoint = new(azure.Endpoint);

        if (!string.IsNullOrWhiteSpace(azure.ApiKey))
        {
            return new AzureOpenAIClient(endpoint, new ApiKeyCredential(azure.ApiKey), options);
        }

        // Pro Tip: unscoped DefaultAzureCredential probes the whole chain
        // (env, workload/managed identity, Visual Studio, Azure CLI...), which is
        // great for local dev but adds cold-start latency in production. There,
        // scope it: new DefaultAzureCredential(new DefaultAzureCredentialOptions
        // { ExcludeVisualStudioCredential = true, ExcludeAzureCliCredential = true })
        // — or use ManagedIdentityCredential directly.
        return new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), options);
    }
}
