namespace AgenticAI.Common.Configuration;

/// <summary>
/// Strongly-typed settings for LLM provider configuration.
/// Bind to the "LlmProvider" section in appsettings.json.
/// </summary>
public sealed record LlmSettings
{
    /// <summary>
    /// The active provider: "azure", "openai", "ollama", "foundry", or "anthropic".
    /// Microsoft Foundry is the Microsoft-recommended path for new MAF projects
    /// (see Chapter 4) and uses the Azure AI Projects client via AIProjectClient.
    /// </summary>
    public string Provider { get; init; } = "ollama";

    /// <summary>Azure OpenAI configuration.</summary>
    public AzureOpenAISettings AzureOpenAI { get; init; } = new();

    /// <summary>OpenAI configuration.</summary>
    public OpenAISettings OpenAI { get; init; } = new();

    /// <summary>Ollama (local) configuration.</summary>
    public OllamaSettings Ollama { get; init; } = new();

    /// <summary>Microsoft Foundry configuration (Azure AI Projects).</summary>
    public FoundrySettings Foundry { get; init; } = new();

    /// <summary>Anthropic (Claude) configuration.</summary>
    public AnthropicSettings Anthropic { get; init; } = new();
}

/// <summary>Azure OpenAI provider settings.</summary>
public sealed record AzureOpenAISettings
{
    public string Endpoint { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-5.6-luna";
}

/// <summary>OpenAI provider settings.</summary>
public sealed record OpenAISettings
{
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = "gpt-5.6-luna";
}

/// <summary>Ollama (local) provider settings.</summary>
public sealed record OllamaSettings
{
    public string Endpoint { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "llama3.2";
}

/// <summary>Microsoft Foundry provider settings (Azure AI Projects).</summary>
public sealed record FoundrySettings
{
    /// <summary>
    /// The Azure AI Projects endpoint, e.g.
    /// "https://my-foundry.services.ai.azure.com/api/projects/my-project".
    /// </summary>
    public string ProjectEndpoint { get; init; } = string.Empty;

    /// <summary>The deployed model name, e.g. "gpt-5.6-luna".</summary>
    public string Model { get; init; } = "gpt-5.6-luna";
}

/// <summary>Anthropic (Claude) provider settings.</summary>
public sealed record AnthropicSettings
{
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>The Claude model id, e.g. "claude-sonnet-4-6".</summary>
    public string Model { get; init; } = "claude-sonnet-4-6";
}
