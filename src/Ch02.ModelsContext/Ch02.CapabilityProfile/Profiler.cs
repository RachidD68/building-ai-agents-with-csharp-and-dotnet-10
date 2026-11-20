using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ch02.CapabilityProfile;

/// <summary>
/// How one trial ended. Each failure has its own bucket: a cut reply is not an
/// invalid one, a refusal is not a wrong answer, and a dropped connection says
/// nothing about the model.
/// </summary>
public enum Outcome { Correct, Wrong, Invalid, Refused, Truncated, TransportError, ProviderError }

/// <summary>A model configuration to profile: a client plus the options sent with every call.</summary>
public sealed record ProfileConfiguration(string Name, string Provider, string Model, IChatClient Client, ChatOptions Options);

public sealed record Trial(
    string TaskId, int Attempt, Outcome Outcome, string? FinishReason,
    long? InputTokens, long? OutputTokens, long? ReasoningTokens, bool ReasoningSeen, string Detail);

public sealed record OptionCheck(string Option, string Requested, string Evidence);

public sealed record ConfigurationResult(
    string Name, string Provider, string Model,
    IReadOnlyList<OptionCheck> Options, IReadOnlyDictionary<Outcome, int> Counts, IReadOnlyList<Trial> Trials);

public static class Profiler
{
    public static async Task<ConfigurationResult> RunAsync(
        ProfileConfiguration config, IReadOnlyList<ProfileTask> tasks, int trials, CancellationToken ct)
    {
        List<Trial> results = [];
        foreach (ProfileTask task in tasks)
        {
            for (int attempt = 1; attempt <= trials; attempt++)
            {
                results.Add(await RunTrialAsync(config, task, attempt, ct));
            }
        }

        Dictionary<Outcome, int> counts = Enum.GetValues<Outcome>()
            .ToDictionary(o => o, o => results.Count(t => t.Outcome == o));
        return new(config.Name, config.Provider, config.Model, CheckOptions(config.Options, results), counts, results);
    }

    static async Task<Trial> RunTrialAsync(ProfileConfiguration config, ProfileTask task, int attempt, CancellationToken ct)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, ContosoTasks.Instructions + "\n" + ContosoTasks.Policy),
            new(ChatRole.User, task.Question),
        ];
        try
        {
            // A clone per call, so a client that edits the options cannot leak into the next trial.
            ChatResponse response = await config.Client.GetResponseAsync(messages, config.Options.Clone(), ct);
            (Outcome outcome, string detail) = Classify(task, response);
            UsageDetails? usage = response.Usage;
            bool reasoningSeen = response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Any();
            return new(task.Id, attempt, outcome, response.FinishReason?.Value,
                usage?.InputTokenCount, usage?.OutputTokenCount, usage?.ReasoningTokenCount, reasoningSeen, detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the caller stopped the run; that is not a model failure
        }
        catch (Exception ex)
        {
            Outcome outcome = IsTransport(ex) ? Outcome.TransportError : Outcome.ProviderError;
            return new(task.Id, attempt, outcome, null, null, null, null, false, Describe(ex));
        }
    }

    /// <summary>
    /// Reads the finish reason before the text: a reply cut by the output cap
    /// is Truncated even when what arrived happens to look like JSON.
    /// </summary>
    public static (Outcome Outcome, string Detail) Classify(ProfileTask task, ChatResponse response)
    {
        string text = response.Text.Trim();
        if (response.FinishReason == ChatFinishReason.Length)
            return (Outcome.Truncated, $"cut at the output cap: {Shorten(text)}");
        if (response.FinishReason == ChatFinishReason.ContentFilter)
            return (Outcome.Refused, "content filter");
        if (LooksLikeRefusal(text))
            return (Outcome.Refused, $"refusal text: {Shorten(text)}");
        if (TryReadDecision(text) is not { } decision)
            return (Outcome.Invalid, $"not the JSON contract: {Shorten(text)}");
        return decision == task.Expected
            ? (Outcome.Correct, decision)
            : (Outcome.Wrong, $"expected {task.Expected}, got {decision}");
    }

    /// <summary>The contract: one JSON object whose "decision" is allow, review or deny.</summary>
    public static string? TryReadDecision(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("decision", out JsonElement d)
                && d.ValueKind == JsonValueKind.String
                && d.GetString() is "allow" or "review" or "deny"
                ? d.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A heuristic, and labelled as one: a provider that refuses with a finish
    // reason says so for certain; a refusal in plain text has to be recognized.
    static bool LooksLikeRefusal(string text) =>
        new[] { "I can't", "I cannot", "I'm sorry", "I am sorry", "I'm unable", "I am unable", "I won't" }
            .Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    static bool IsTransport(Exception ex) => ex switch
    {
        HttpRequestException or TimeoutException or IOException => true,
        TaskCanceledException => true,                     // HttpClient.Timeout, not the caller
        _ => ex.InnerException is { } inner && IsTransport(inner),
    };

    /// <summary>
    /// What the responses show about each requested option. A value in
    /// ChatOptions is a request; only the response can show it was applied.
    /// </summary>
    public static List<OptionCheck> CheckOptions(ChatOptions options, IReadOnlyList<Trial> trials)
    {
        List<OptionCheck> checks = [];
        List<Trial> answered = [.. trials.Where(t => t.Outcome is not (Outcome.TransportError or Outcome.ProviderError))];

        if (options.MaxOutputTokens is int cap)
        {
            List<long> outputs = [.. answered.Where(t => t.OutputTokens is not null).Select(t => t.OutputTokens!.Value)];
            string evidence = outputs.Count == 0 ? "not observable: no usage reported"
                : outputs.Max() > cap ? $"IGNORED: a reply used {outputs.Max()} output tokens"
                : $"honored: max {outputs.Max()} output tokens, {answered.Count(t => t.Outcome == Outcome.Truncated)} truncated";
            checks.Add(new("MaxOutputTokens", cap.ToString(), evidence));
        }
        if (options.Reasoning is { } reasoning)
        {
            string evidence = trials.Count > 0 && answered.Count == 0 ? "rejected: every call failed"
                : answered.Any(t => t.ReasoningSeen || t.ReasoningTokens > 0) ? "observed: reasoning content or tokens"
                : "NO EVIDENCE: no reasoning content and no reasoning tokens";
            checks.Add(new("Reasoning", $"Effort={reasoning.Effort}", evidence));
        }
        if (options.ResponseFormat is ChatResponseFormatJson)
        {
            int parsed = answered.Count(t => t.Outcome is Outcome.Correct or Outcome.Wrong);
            checks.Add(new("ResponseFormat", "Json", $"{parsed}/{answered.Count} answered replies met the JSON contract"));
        }
        if (options.Temperature is float temperature)
            checks.Add(new("Temperature", temperature.ToString("0.0#"), "sent; not observable from the response"));
        if (options.TopP is float topP)
            checks.Add(new("TopP", topP.ToString("0.0#"), "sent; not observable from the response"));
        if (options.Seed is long seed)
            checks.Add(new("Seed", seed.ToString(), "sent; not observable from the response"));
        return checks;
    }

    static string Describe(Exception ex)
    {
        string message = ex.Message.ReplaceLineEndings(" ");
        return $"{ex.GetType().Name}: {Shorten(message)}";
    }

    static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "...";
}
