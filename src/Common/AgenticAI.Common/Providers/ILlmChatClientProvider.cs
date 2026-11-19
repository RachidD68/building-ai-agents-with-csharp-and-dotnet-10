using Microsoft.Extensions.AI;
using AgenticAI.Common.Configuration;

namespace AgenticAI.Common.Providers;

/// <summary>
/// One arm of the provider switch behind
/// <see cref="Extensions.ServiceCollectionExtensions.AddLlmChatClient"/>.
/// The core package ships the GA arms (<c>openai</c> and <c>ollama</c>); an
/// optional package adds another arm by registering its own provider, so the
/// core never references that package. AgenticAI.Common.Azure does this for
/// <c>azure</c>:
/// <code>
/// builder.Services.AddAzureOpenAIProvider();          // AgenticAI.Common.Azure
/// builder.Services.AddLlmChatClient(builder.Configuration);
/// </code>
/// </summary>
public interface ILlmChatClientProvider
{
    /// <summary>
    /// The value of <c>LlmProvider:Provider</c> that selects this arm, matched
    /// without regard to case (for example "openai").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The configuration key of the first required setting that is missing
    /// (for example "LlmProvider:OpenAI:ApiKey"), or <see langword="null"/>
    /// when the arm has what it needs. Checked before any client is built, so
    /// a sample fails with the setting's name instead of an SDK error.
    /// </summary>
    string? FindMissingSetting(LlmSettings settings);

    /// <summary>
    /// Builds the chat client over <paramref name="http"/>, the named "llm"
    /// HttpClient that carries the resilience handler. The arm should turn off
    /// its SDK's own retries so the handler stays the single retry owner.
    /// </summary>
    IChatClient CreateChatClient(HttpClient http, LlmSettings settings);
}
