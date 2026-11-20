using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Ch18.AguiClient;

/// <summary>
/// One AG-UI run as the client shows it. Events are applied by identity:
/// run events only for the run id this client sent, text by message id (a
/// second START for a message is ignored), tool status by tool call id.
/// Content for a message that never started, or a stream that ends without
/// RUN_FINISHED or RUN_ERROR, is a gap: the partial text is discarded and the
/// client resyncs the operation from the status snapshot. AG-UI events from
/// this host carry no SSE id, so a cut run cannot resume from a cursor.
/// RUN_FINISHED ends the response; it never means the operation is done.
/// </summary>
public sealed class AguiRunView(string runId)
{
    private readonly Dictionary<string, StringBuilder> _messages = [];
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _tools = [];

    public string RunId { get; } = runId;
    public bool Started { get; private set; }
    public bool Finished { get; private set; }
    public string? Error { get; private set; }
    public bool NeedsResync { get; private set; }
    public int Applied { get; private set; }
    public int Discarded { get; private set; }
    public IReadOnlyDictionary<string, string> Tools => _tools;
    public string Text => NeedsResync ? "" : string.Concat(_order.Select(id => _messages[id].ToString()));

    public void Apply(JsonElement e)
    {
        string type = e.GetProperty("type").GetString() ?? "";
        if (type.StartsWith("RUN_", StringComparison.Ordinal) && e.TryGetProperty("runId", out JsonElement run)
            && run.GetString() != RunId)
        {
            Discarded++;   // another run's lifecycle event
            return;
        }
        string? message = e.TryGetProperty("messageId", out JsonElement m) ? m.GetString() : null;
        string? tool = e.TryGetProperty("toolCallId", out JsonElement t) ? t.GetString() : null;
        switch (type)
        {
            case "RUN_STARTED": Started = true; break;
            case "RUN_FINISHED": Finished = true; break;
            case "RUN_ERROR": Error = e.TryGetProperty("message", out JsonElement msg) ? msg.GetString() : "error"; break;
            case "TEXT_MESSAGE_START" when message is not null:
                if (!_messages.TryAdd(message, new StringBuilder())) { Discarded++; return; }
                _order.Add(message);
                break;
            case "TEXT_MESSAGE_CONTENT" when message is not null:
                if (!_messages.TryGetValue(message, out StringBuilder? text)) { NeedsResync = true; return; }
                text.Append(e.GetProperty("delta").GetString());
                break;
            case "TOOL_CALL_START" when tool is not null:
                _tools[tool] = $"{e.GetProperty("toolCallName").GetString()}: started";
                break;
            case "TOOL_CALL_RESULT" when tool is not null && _tools.TryGetValue(tool, out string? started):
                _tools[tool] = started.Replace("started", "finished", StringComparison.Ordinal);
                break;
        }
        Applied++;
    }

    /// <summary>The connection closed. Without RUN_FINISHED or RUN_ERROR the run is incomplete.</summary>
    public void EndOfStream()
    {
        if (!Finished && Error is null) NeedsResync = true;
    }
}

/// <summary>Posts one AG-UI run (RunAgentInput) and reads its events with SseParser.</summary>
public static class AguiStream
{
    public static string Input(string thread, string run, string message) => JsonSerializer.Serialize(new
    {
        threadId = thread, runId = run,
        messages = new[] { new { id = "msg_" + Guid.NewGuid().ToString("N")[..8], role = "user", content = message } },
        tools = Array.Empty<object>(), context = Array.Empty<object>(), state = new { }, forwardedProps = new { },
    });

    public static async IAsyncEnumerable<JsonElement> RunAsync(HttpClient http, string path, string token,
        string thread, string run, string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(Input(thread, run, message), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"AG-UI run refused: {(int)response.StatusCode}", null, response.StatusCode);
        await using Stream body = await response.Content.ReadAsStreamAsync(ct);
        await foreach (SseItem<JsonElement> item in SseParser.Create(body, (_, data) =>
            JsonDocument.Parse(data.ToArray()).RootElement.Clone()).EnumerateAsync(ct))
            yield return item.Data;
    }
}
