using Microsoft.Extensions.Configuration;

namespace AgenticAI.Common.Configuration;

/// <summary>
/// Explains which LLM settings a sample will use, and where each one comes from,
/// without calling a model. Run any sample with <c>--config-check</c>:
/// <code>dotnet run -- --config-check</code>
/// It prints setting NAMES and their sources (appsettings.json, user secrets,
/// environment variables) — never their values — so the output is safe to paste
/// into an issue.
/// </summary>
public static class LlmConfigCheck
{
    /// <summary>
    /// Prints the report and exits the process when <c>--config-check</c> is on the
    /// command line; otherwise does nothing.
    /// </summary>
    public static void RunIfRequested(IConfiguration configuration)
    {
        if (!Environment.GetCommandLineArgs().Contains(Flag, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // The environment the host already used to choose its sources.
        string? hostEnvironment = configuration[EnvironmentKey];
        DropFlagFromCommandLine(configuration);
        Console.WriteLine(Report(configuration, hostEnvironment));
        Environment.Exit(0);
    }

    const string Flag = "--config-check";
    const string EnvironmentKey = "environment";   // set by DOTNET_ENVIRONMENT or --environment

    /// <summary>
    /// The command-line provider reads a bare <c>--config-check</c> as a key and takes
    /// the next argument as its value, so a setting written after the flag would vanish
    /// from the report. This removes the flag from every command-line source and reloads.
    /// </summary>
    public static void DropFlagFromCommandLine(IConfiguration configuration)
    {
        if (configuration is not IConfigurationBuilder builder)
        {
            return;
        }

        for (int i = 0; i < builder.Sources.Count; i++)
        {
            IConfigurationSource source = builder.Sources[i];
            if (source.GetType().GetProperty("Args") is { CanWrite: true } argsProperty
                && argsProperty.GetValue(source) is IEnumerable<string> sourceArgs
                && sourceArgs.Contains(Flag, StringComparer.OrdinalIgnoreCase))
            {
                argsProperty.SetValue(source, sourceArgs
                    .Where(a => !a.Equals(Flag, StringComparison.OrdinalIgnoreCase)).ToArray());
                builder.Sources[i] = source;   // a ConfigurationManager rebuilds its providers
            }
        }
    }

    /// <summary>Builds the report: names and sources only, never values.</summary>
    public static string Report(IConfiguration configuration) =>
        Report(configuration, configuration[EnvironmentKey]);

    static string Report(IConfiguration configuration, string? hostEnvironment)
    {
        LlmSettings settings = new();
        configuration.GetSection("LlmProvider").Bind(settings);
        string provider = settings.Provider.ToLowerInvariant();

        List<string> lines =
        [
            "=== LLM configuration check (no model is called) ===",
            $"Environment  : {hostEnvironment ?? "Production (not set)"}",
            $"Provider     : {settings.Provider}   (from {SourceOf(configuration, "LlmProvider:Provider") ?? "the built-in default"})",
            $"User secrets : {(UserSecretsLoaded(configuration) ? "loaded" : "not loaded - they load only when the environment is Development")}",
        ];
        if (!string.Equals(configuration[EnvironmentKey], hostEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("Note         : --environment came after --config-check, too late for the host; put --config-check last");
        }

        (string Key, bool Required, string Hint)[] keys = provider switch
        {
            "azure" =>
            [
                ("LlmProvider:AzureOpenAI:Endpoint", true, "https://YOUR-RESOURCE.openai.azure.com/"),
                ("LlmProvider:AzureOpenAI:Model", true, "your deployment name"),
                ("LlmProvider:AzureOpenAI:ApiKey", false, "optional: without it, DefaultAzureCredential is used (run az login)"),
            ],
            "openai" =>
            [
                ("LlmProvider:OpenAI:ApiKey", true, "your OpenAI API key"),
                ("LlmProvider:OpenAI:Model", true, "e.g. gpt-5.6-luna"),
            ],
            "anthropic" =>
            [
                ("LlmProvider:Anthropic:ApiKey", true, "or the ANTHROPIC_API_KEY environment variable"),
                ("LlmProvider:Anthropic:Model", true, "e.g. claude-sonnet-4-6"),
            ],
            "foundry" =>
            [
                ("LlmProvider:Foundry:ProjectEndpoint", true, "https://NAME.services.ai.azure.com/api/projects/PROJECT"),
                ("LlmProvider:Foundry:Model", true, "your model deployment name"),
            ],
            _ =>
            [
                ("LlmProvider:Ollama:Endpoint", false, $"default {new OllamaSettings().Endpoint}"),
                ("LlmProvider:Ollama:Model", false, $"default {new OllamaSettings().Model}; run: ollama pull <model>"),
            ],
        };

        foreach ((string key, bool required, string hint) in keys)
        {
            string? source = SourceOf(configuration, key);
            if (source is null && key == "LlmProvider:Anthropic:ApiKey"
                && !string.IsNullOrWhiteSpace(configuration["ANTHROPIC_API_KEY"]))
            {
                source = "ANTHROPIC_API_KEY";
            }

            string state = source is not null ? "set" : required ? "MISSING" : "not set";
            lines.Add($"  {key,-38} {state,-8} {(source is not null ? $"(from {source})" : $"- {hint}")}");
        }

        lines.Add("Set a value with user secrets, e.g.: dotnet user-secrets set \"LlmProvider:Provider\" \"openai\"");
        lines.Add("or an environment variable, e.g.:  LlmProvider__OpenAI__ApiKey");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The configuration source that supplies a non-empty value for <paramref name="key"/>, if any.</summary>
    public static string? SourceOf(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return string.IsNullOrWhiteSpace(configuration[key]) ? null : "configuration";
        }

        // Later providers win, so walk them from last to first.
        foreach (IConfigurationProvider provider in root.Providers.Reverse())
        {
            if (provider.TryGet(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return Describe(provider);
            }
        }
        return null;
    }

    static bool UserSecretsLoaded(IConfiguration configuration) =>
        configuration is IConfigurationRoot root
        && root.Providers.Any(p => p.ToString()?.Contains("secrets.json", StringComparison.OrdinalIgnoreCase) == true);

    static string Describe(IConfigurationProvider provider)
    {
        string text = provider.ToString() ?? provider.GetType().Name;
        if (text.Contains("secrets.json", StringComparison.OrdinalIgnoreCase)) return "user secrets";
        if (text.Contains("EnvironmentVariables", StringComparison.OrdinalIgnoreCase)) return "environment variables";
        if (text.Contains("CommandLine", StringComparison.OrdinalIgnoreCase)) return "command line";
        int quote = text.IndexOf('\'');
        return quote >= 0 && text.IndexOf('\'', quote + 1) is int end and > 0
            ? text[(quote + 1)..end]                     // e.g. appsettings.json
            : text;
    }
}
