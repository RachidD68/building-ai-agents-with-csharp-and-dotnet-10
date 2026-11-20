using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Configuration;
using AgenticAI.Common.Extensions;

namespace Ch03.FirstLlmCall;

/// <summary>
/// The configuration rules of this chapter, checked over in-memory
/// configurations and host builders: no model, no network, and an empty scratch
/// folder in place of your user-secrets store. The wrong-root and flag-order cases
/// reproduce the defect first, then show the fix; the others check a rule.
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
        Expect("Development -> user secrets are a configuration source",
            AskHost(["--environment", "Development"], c => SecretsIndex(c) >= 0));
        Expect("Production  -> user secrets are not loaded",
            AskHost(["--environment", "Production"], c => SecretsIndex(c) < 0));
        Expect("Development -> the host adds environment variables after user secrets",
            AskHost(["--environment", "Development"], c => SecretsIndex(c) is >= 0 and int s && LastIndexOf(c, "EnvironmentVariables") > s));
        Expect("--environment Development -> the report says Development and user secrets loaded",
            AskHost(["--environment", "Development"], c => LlmConfigCheck.Report(c) is string r
                && Line(r, "Environment  :").Contains("Development") && Line(r, "User secrets :").Contains(": loaded")));

        Console.WriteLine("\n-- A setting written after --config-check --");
        Expect("DEFECT REPRODUCED: the setting is read as the flag's value -> not from the command line",
            AskHost(["--config-check", "--LlmProvider:Provider=openai"],
                c => !Line(LlmConfigCheck.Report(c), "Provider     :").Contains("(from command line)")));
        Expect("fixed by dropping the flag from the command line -> openai (from command line)",
            AskHost(["--config-check", "--LlmProvider:Provider=openai"], c =>
            {
                LlmConfigCheck.DropFlagFromCommandLine(c);
                return Line(LlmConfigCheck.Report(c), "Provider     :").Contains("openai   (from command line)");
            }));

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

    // The host builder decides; the test asks it which sources it added. User secrets
    // point at an empty scratch folder, so your real secrets file is never read.
    static bool AskHost(string[] args, Func<IConfigurationRoot, bool> check)
    {
        string scratch = Directory.CreateTempSubdirectory("ch03-selftest-").FullName;
        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        string? home = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("APPDATA", scratch);
        Environment.SetEnvironmentVariable("HOME", scratch);
        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ApplicationName = typeof(ConfigSelfTest).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
            });
            using ConfigurationManager configuration = builder.Configuration;
            return check(configuration);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        host builder failed: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", appData);
            Environment.SetEnvironmentVariable("HOME", home);
            Directory.Delete(scratch, recursive: true);
        }
    }

    static int SecretsIndex(IConfigurationRoot configuration) => LastIndexOf(configuration, "secrets.json");

    static int LastIndexOf(IConfigurationRoot configuration, string providerText) =>
        configuration.Providers.Select((p, i) => p.ToString()?.Contains(providerText, StringComparison.OrdinalIgnoreCase) == true ? i : -1).Max();

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
