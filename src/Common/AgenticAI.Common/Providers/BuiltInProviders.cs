using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using AgenticAI.Common.Configuration;

namespace AgenticAI.Common.Providers;

/// <summary>The <c>openai</c> arm: the OpenAI SDK (GA) over the "llm" HttpClient.</summary>
public sealed class OpenAIChatClientProvider : ILlmChatClientProvider
{
    public string Name => "openai";

    public string? FindMissingSetting(LlmSettings settings) =>
        string.IsNullOrWhiteSpace(settings.OpenAI.ApiKey) ? "LlmProvider:OpenAI:ApiKey" : null;

    public IChatClient CreateChatClient(HttpClient http, LlmSettings settings) =>
        new OpenAI.OpenAIClient(
                new ApiKeyCredential(settings.OpenAI.ApiKey),
                new OpenAI.OpenAIClientOptions
                {
                    Transport = new HttpClientPipelineTransport(http),
                    // One retry owner: the resilience handler on the "llm"
                    // HttpClient. Left on, the SDK's own retries would
                    // multiply with it (4 x 4 = 16 sends for one call).
                    RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
                })
            .GetChatClient(settings.OpenAI.Model)
            .AsIChatClient();
}

/// <summary>The <c>ollama</c> arm: a local model through OllamaSharp (GA).</summary>
public sealed class OllamaChatClientProvider : ILlmChatClientProvider
{
    public string Name => "ollama";

    // Endpoint and model both have working defaults (localhost:11434, llama3.2).
    public string? FindMissingSetting(LlmSettings settings) => null;

    public IChatClient CreateChatClient(HttpClient http, LlmSettings settings)
    {
        // OllamaSharp takes the endpoint from the HttpClient's BaseAddress;
        // CreateClient returns a fresh instance, so setting it here is safe.
        http.BaseAddress = new Uri(settings.Ollama.Endpoint);
        return new OllamaSharp.OllamaApiClient(http, settings.Ollama.Model);
    }
}
