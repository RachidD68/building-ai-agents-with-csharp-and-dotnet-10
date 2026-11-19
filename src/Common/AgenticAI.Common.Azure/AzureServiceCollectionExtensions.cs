using Microsoft.Extensions.DependencyInjection;
using AgenticAI.Common.Azure;

namespace AgenticAI.Common.Extensions;

/// <summary>Registration for the optional Azure OpenAI arm.</summary>
public static class AzureServiceCollectionExtensions
{
    /// <summary>
    /// Plugs the <c>azure</c> arm into <see cref="ServiceCollectionExtensions.AddLlmChatClient"/>,
    /// so <c>LlmProvider:Provider = "azure"</c> selects an Azure OpenAI deployment
    /// (settings under <c>LlmProvider:AzureOpenAI</c>). Call it before or after
    /// AddLlmChatClient; the order does not matter.
    /// </summary>
    public static IServiceCollection AddAzureOpenAIProvider(this IServiceCollection services) =>
        services.AddLlmChatClientProvider(new AzureOpenAIChatClientProvider());
}
