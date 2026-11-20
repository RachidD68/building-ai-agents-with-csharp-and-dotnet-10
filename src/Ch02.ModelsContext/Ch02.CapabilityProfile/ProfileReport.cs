using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Ch02.CapabilityProfile;

/// <summary>A dated profile: what ran, with which package pins, and what was not run.</summary>
public sealed record CapabilityReport(
    DateTimeOffset RunAtUtc, IReadOnlyDictionary<string, string> Pins, int TrialsPerTask,
    IReadOnlyList<ConfigurationResult> Configurations, IReadOnlyList<string> NotRun)
{
    static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The versions that make this profile reproducible (or not).</summary>
    public static Dictionary<string, string> CurrentPins(params Type[] connectorTypes)
    {
        Dictionary<string, string> pins = new() { [".NET"] = Environment.Version.ToString() };
        foreach (Type type in connectorTypes.Prepend(typeof(IChatClient)))
        {
            Assembly assembly = type.Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "unknown";
            pins[assembly.GetName().Name!] = version.Split('+')[0];
        }
        return pins;
    }

    public string ToJson() => JsonSerializer.Serialize(this, s_json);

    public string ToMarkdown()
    {
        StringBuilder md = new();
        md.AppendLine($"# Capability profile, {RunAtUtc:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine();
        md.AppendLine($"Pins: {string.Join(", ", Pins.Select(p => $"{p.Key} {p.Value}"))}. " +
                      $"Task set: {ContosoTasks.All.Length} Contoso policy questions, {TrialsPerTask} trial(s) each.");
        md.AppendLine();
        md.AppendLine("| Configuration | Provider | Model | Correct | Wrong | Invalid | Refused | Truncated | Transport | Provider error |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (ConfigurationResult c in Configurations)
        {
            md.AppendLine($"| {c.Name} | {c.Provider} | {c.Model} | {c.Counts[Outcome.Correct]} | {c.Counts[Outcome.Wrong]} | " +
                          $"{c.Counts[Outcome.Invalid]} | {c.Counts[Outcome.Refused]} | {c.Counts[Outcome.Truncated]} | " +
                          $"{c.Counts[Outcome.TransportError]} | {c.Counts[Outcome.ProviderError]} |");
        }
        md.AppendLine();
        md.AppendLine("## Options: requested and observed");
        md.AppendLine();
        md.AppendLine("| Configuration | Option | Requested | Evidence |");
        md.AppendLine("|---|---|---|---|");
        foreach (ConfigurationResult c in Configurations)
            foreach (OptionCheck o in c.Options)
                md.AppendLine($"| {c.Name} | {o.Option} | {o.Requested} | {o.Evidence} |");
        md.AppendLine();
        md.AppendLine("## Failed trials");
        md.AppendLine();
        List<string> failed = [.. Configurations.SelectMany(c => c.Trials
            .Where(t => t.Outcome != Outcome.Correct)
            .Select(t => $"- {c.Name} / {t.TaskId} #{t.Attempt}: {t.Outcome}" +
                         $"{(t.FinishReason is null ? "" : $" (finish: {t.FinishReason})")}: {t.Detail.Replace("|", "/")}"))];
        md.AppendLine(failed.Count == 0 ? "None." : string.Join(Environment.NewLine, failed));
        md.AppendLine();
        md.AppendLine("## Not run");
        md.AppendLine();
        md.AppendLine(NotRun.Count == 0 ? "None." : string.Join(Environment.NewLine, NotRun.Select(n => $"- {n}")));
        return md.ToString();
    }

    /// <summary>Writes capability-profile-yyyy-MM-dd.json and .md into <paramref name="folder"/>.</summary>
    public (string Json, string Markdown) Write(string folder)
    {
        Directory.CreateDirectory(folder);
        string stem = Path.Combine(folder, $"capability-profile-{RunAtUtc:yyyy-MM-dd}");
        File.WriteAllText(stem + ".json", ToJson(), new UTF8Encoding(false));
        File.WriteAllText(stem + ".md", ToMarkdown(), new UTF8Encoding(false));
        return (stem + ".json", stem + ".md");
    }
}
