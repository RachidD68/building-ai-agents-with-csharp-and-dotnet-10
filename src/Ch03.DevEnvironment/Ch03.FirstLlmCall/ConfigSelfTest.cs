using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Configuration;
using AgenticAI.Common.Extensions;

namespace Ch03.FirstLlmCall;

/// <summary>
/// The configuration rules of this chapter, checked over in-memory
/// configurations: no model, no network, no file outside the build output.
/// Every case that shows a failure first reproduces it, then shows the fix.
/// </summary>
internal static class ConfigSelfTest
{
    // Placeholders that stand in for secrets. The report must never print them.
    const string FileKey = "sk-from-appsettings-0000";
    const string EnvKey = "sk-from-environment-1111";
    const string EnvPrefix = "CH03SELFTEST_";

    static int s_passed, s_total;

    public static int Run()
    {
        Console.WriteLine("=== First LLM call: configuration self-test (no model is called) ===");

        Console.WriteLine("\n-- Wrong root: AzureOpenAI:Endpoint outside LlmProvider --");
        string wrongRoot = LlmConfigCheck.Report(Memory(
            ("LlmProvider:Provider", "azure"),
            ("AzureOpenAI:Endpoint", "https://contoso-dev.openai.azure.com/"),
            ("AzureOpenAI:Model", "chat")));
        Expect("DEFECT REPRODUCED: a root-level AzureOpenAI:Endpoint is ignored -> MISSING",
            Line(wrongRoot, "LlmProvider:AzureOpenAI:Endpoint").Contains("MISSING"));
        string rightRoot = LlmConfigCheck.Report(Memory(
            ("LlmProvider:Provider", "azure"),
            ("LlmProvider:AzureOpenAI:Endpoint", "https://contoso-dev.openai.azure.com/"),
            ("LlmProvider:AzureOpenAI:Model", "chat")));
        Expect("fixed under LlmProvider: -> set",
            Line(rightRoot, "LlmProvider:AzureOpenAI:Endpoint").Contains(" set "));

        Console.WriteLine("\n-- Missing key: openai without an ApiKey --");
        IConfiguration noKey = Memory(("LlmProvider:Provider", "openai"), ("LlmProvider:OpenAI:Model", "gpt-5.6-luna"));
        Expect("the report marks LlmProvider:OpenAI:ApiKey MISSING",
            Line(LlmConfigCheck.Report(noKey), "LlmProvider:OpenAI:ApiKey").Contains("MISSING"));
        string? thrown = RegistrationError(noKey);
        Expect("AddLlmChatClient fails at registration, before any call, and names the setting",
            thrown?.Contains("LlmProvider:OpenAI:ApiKey") == true);
        Expect("with the key set, registration succeeds",
            RegistrationError(Memory(("LlmProvider:Provider", "openai"), ("LlmProvider:OpenAI:ApiKey", FileKey))) is null);

        Console.WriteLine("\n-- Order of sources: a later source wins --");
        Environment.SetEnvironmentVariable(EnvPrefix + "LlmProvider__OpenAI__ApiKey", EnvKey);
        try
        {
            IConfigurationRoot layered = new ConfigurationBuilder()
                .AddInMemoryCollection(Pairs(("LlmProvider:Provider", "openai"), ("LlmProvider:OpenAI:ApiKey", FileKey)))
                .AddEnvironmentVariables(EnvPrefix)
                .Build();
            Expect("the environment variable beats the earlier source",
                layered["LlmProvider:OpenAI:ApiKey"] == EnvKey);
            string report = LlmConfigCheck.Report(layered);
            Expect("--config-check names the winning source: environment variables",
                Line(report, "LlmProvider:OpenAI:ApiKey").Contains("(from environment variables)"));
            Expect("--config-check prints no value, from either source",
                !report.Contains(FileKey) && !report.Contains(EnvKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvPrefix + "LlmProvider__OpenAI__ApiKey", null);
        }

        Console.WriteLine("\n-- Defaults and fallbacks --");
        string empty = LlmConfigCheck.Report(Memory());
        Expect("no Provider anywhere -> ollama, from the built-in default",
            Line(empty, "Provider     :").Contains("ollama") && empty.Contains("the built-in default"));
        string anthropic = LlmConfigCheck.Report(Memory(
            ("LlmProvider:Provider", "anthropic"), ("ANTHROPIC_API_KEY", FileKey)));
        Expect("anthropic: ANTHROPIC_API_KEY counts as the key",
            Line(anthropic, "LlmProvider:Anthropic:ApiKey").Contains("(from ANTHROPIC_API_KEY)"));

        Console.WriteLine("\n-- User secrets load only in Development --");
        Expect("Development -> user secrets are a configuration source", HasUserSecrets("Development"));
        Expect("Production  -> user secrets are not loaded", !HasUserSecrets("Production"));

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static IConfigurationRoot Memory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(Pairs(pairs)).Build();

    static IEnumerable<KeyValuePair<string, string?>> Pairs(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value));

    static string Line(string report, string key) =>
        report.Split(Environment.NewLine).FirstOrDefault(l => l.Contains(key)) ?? "";

    static string? RegistrationError(IConfiguration configuration)
    {
        try
        {
            new ServiceCollection().AddLlmChatClient(configuration);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    // The host builder decides; the test only asks it which sources it added.
    static bool HasUserSecrets(string environment)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
            ApplicationName = typeof(ConfigSelfTest).Assembly.GetName().Name,
            Args = [],
        });
        return ((IConfigurationRoot)builder.Configuration).Providers
            .Any(p => p.ToString()?.Contains("secrets.json", StringComparison.OrdinalIgnoreCase) == true);
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
