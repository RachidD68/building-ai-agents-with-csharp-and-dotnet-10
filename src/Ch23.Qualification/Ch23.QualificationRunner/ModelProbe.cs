using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Ch23.QualificationRunner;

/// <summary>
/// The model's identity as the provider reports it, not as the configuration names it:
/// the Ollama digest of the pulled model (GET /api/tags) and ChatResponse.ModelId from one
/// short call. A name like "llama3.2" can point at another digest after a pull.
/// </summary>
public static class ModelProbe
{
    public static async Task<RawRun> RunAsync(ModelProfile profile, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        StringBuilder o = new();
        int ok = 0;
        using CancellationTokenSource cts = new(timeout);
        try
        {
            using HttpClient http = new() { BaseAddress = new Uri(profile.Endpoint), Timeout = timeout };
            using JsonDocument tags = JsonDocument.Parse(await http.GetStringAsync("/api/tags", cts.Token));
            JsonElement? model = tags.RootElement.GetProperty("models").EnumerateArray()
                .Where(m => m.GetProperty("name").GetString() is { } n && (n == profile.ChatModel || n == profile.ChatModel + ":latest"))
                .Select(m => (JsonElement?)m).FirstOrDefault();
            if (model is { } found)
            {
                o.AppendLine($"name: {found.GetProperty("name").GetString()}");
                o.AppendLine($"digest: {found.GetProperty("digest").GetString()}");
                o.AppendLine($"size: {found.GetProperty("size").GetInt64()}");
                ok++;
            }
            else o.AppendLine($"digest: (no pulled model named {profile.ChatModel})");

            using IChatClient chat = new OllamaApiClient(http, profile.ChatModel);
            ChatResponse r = await chat.GetResponseAsync("Reply with the single word OK.",
                new ChatOptions { Temperature = 0f, MaxOutputTokens = 5 }, cts.Token);
            o.AppendLine($"response model id: {r.ModelId ?? "(null)"}");
            o.AppendLine($"usage: input {r.Usage?.InputTokenCount}, output {r.Usage?.OutputTokenCount}");
            if (!string.IsNullOrEmpty(r.ModelId)) ok++;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            o.AppendLine($"probe error: {ex.GetType().Name}: {ex.Message}");
        }
        o.AppendLine($"RESULT {ok}/2");
        return new RawRun(ok == 2 ? 0 : 1, o.ToString().TrimEnd(), clock.Elapsed.TotalSeconds);
    }

    public static ObservedModel? Read(string? log, string name)
    {
        if (log is null || !File.Exists(log)) return null;
        string[] lines = File.ReadAllLines(log);
        string? V(string label) => lines.FirstOrDefault(l => l.StartsWith(label, StringComparison.Ordinal))?[label.Length..].Trim();
        string? digest = V("digest:");
        return new ObservedModel(V("name:") ?? name, digest is { } d && !d.StartsWith('(') ? d : null,
            long.TryParse(V("size:"), out long size) ? size : null, V("response model id:"), V("probe error:"));
    }
}
