using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OllamaSharp;

namespace Ex01.MultiProviderChat;

/// <summary>Picks and builds the IChatClient from LlmProvider:Provider.</summary>
public static class ProviderSwitch
{
    public const string ProviderKey = "LlmProvider:Provider";

    /// <summary>The active provider; ollama when nothing sets it.</summary>
    public static string Name(IConfiguration configuration) =>
        configuration[ProviderKey] is { Length: > 0 } name ? name.Trim().ToLowerInvariant() : "ollama";

    /// <summary>One line for --config-check: the provider and its source, never a value.</summary>
    public static string Describe(IConfiguration configuration) =>
        $"Provider: {Name(configuration)}   (from {SourceOf(configuration, ProviderKey) ?? "the built-in default"})";

    public static IChatClient Create(IConfiguration configuration) => Name(configuration) switch
    {
        "ollama" => new OllamaApiClient(
            new Uri(configuration["LlmProvider:Ollama:Endpoint"] is { Length: > 0 } e ? e : "http://localhost:11434"),
            configuration["LlmProvider:Ollama:Model"] is { Length: > 0 } m ? m : "llama3.2"),

        "openai" => new OpenAI.OpenAIClient(Required(configuration, "LlmProvider:OpenAI:ApiKey"))
            .GetChatClient(Required(configuration, "LlmProvider:OpenAI:Model"))
            .AsIChatClient(),

        // No key here: DefaultAzureCredential, which still needs a role assignment.
        "azure" => new AzureOpenAIClient(
                new Uri(Required(configuration, "LlmProvider:AzureOpenAI:Endpoint")),
                new DefaultAzureCredential())
            .GetChatClient(Required(configuration, "LlmProvider:AzureOpenAI:Model"))
            .AsIChatClient(),

        string other => throw new InvalidOperationException(
            $"Unknown provider '{other}' in {ProviderKey}. Valid values: ollama, openai, azure."),
    };

    // "" counts as missing: appsettings.json ships empty placeholders.
    static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Provider '{Name(configuration)}' needs {key}. Set it with dotnet user-secrets set \"{key}\" \"<value>\" " +
                $"or the environment variable {key.Replace(":", "__")}.");

    // Later sources win, so walk them from last to first.
    static string? SourceOf(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return configuration[key] is { Length: > 0 } ? "configuration" : null;
        }
        IConfigurationProvider? winner = root.Providers.Reverse()
            .FirstOrDefault(p => p.TryGet(key, out string? v) && !string.IsNullOrWhiteSpace(v));
        string? text = winner?.ToString();
        return text switch
        {
            null => null,
            _ when text.Contains("secrets.json") => "user secrets",
            _ when text.Contains("EnvironmentVariables") => "environment variables",
            _ when text.Contains("CommandLine") => "command line",
            _ when text.Contains("appsettings") => text.Split('\'')[1],   // e.g. appsettings.json
            _ => winner!.GetType().Name,
        };
    }
}

/// <summary>One chat turn. Works with any IChatClient, the fake one included.</summary>
public static class ChatTurn
{
    public static async Task<string> AskAsync(IChatClient chat, string input, CancellationToken ct = default)
    {
        ChatResponse response = await chat.GetResponseAsync(input, cancellationToken: ct);
        string tokens = response.Usage is { } u
            ? $"\n  [tokens: {u.InputTokenCount ?? 0} in, {u.OutputTokenCount ?? 0} out]"
            : "";
        return $"Assistant: {response.Text}{tokens}";
    }
}
