using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ch18.SseStatusStream;

/// <summary>
/// The status stream over HTTP. The host decides who may read a conversation
/// (<paramref name="canRead"/>); a caller who may not gets 404, the same answer
/// as for an id that does not exist.
/// </summary>
public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusStream(this IEndpointRouteBuilder app, string prefix,
        OperationLog log, Func<HttpContext, string, bool> canRead)
    {
        RouteGroupBuilder group = app.MapGroup(prefix);
        group.MapGet("/{id}/snapshot", (string id, HttpContext http) =>
            canRead(http, id) ? Results.Ok(log.Snapshot(id)) : Results.NotFound());

        // Resume from Last-Event-ID (what EventSource sends) or ?after=. The stream
        // ends after a terminal state of the current run, or with a resync event
        // when this client's queue overflowed.
        group.MapGet("/{id}/events", (string id, HttpContext http, CancellationToken ct) =>
        {
            if (!canRead(http, id)) return Results.NotFound();
            long after = ParseCursor(http.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? http.Request.Query["after"].FirstOrDefault());
            return TypedResults.ServerSentEvents(Stream(log, id, after, ct));
        });
        return group;
    }

    public static long ParseCursor(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long c) ? c : 0;

    internal static async IAsyncEnumerable<SseItem<StatusEvent>> Stream(OperationLog log, string id, long after,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Subscription sub = log.Subscribe(id, after);
        try
        {
            string? run = sub.RunId;
            foreach (StatusEvent e in sub.Backlog)
            {
                run = e.Type == EventTypes.Snapshot || IsRunStart(e) ? e.RunId : run;
                yield return Item(e);
                if (e.RunId == run && (e.Type is EventTypes.State or EventTypes.Snapshot) && OperationStates.IsTerminal(e.State)) yield break;
            }
            await foreach (StatusEvent e in sub.Queue.Reader.ReadAllAsync(ct))
            {
                run = IsRunStart(e) ? e.RunId : run;
                yield return Item(e);
                if (e.RunId == run && e.Type == EventTypes.State && OperationStates.IsTerminal(e.State)) yield break;
            }
            if (sub.Overflowed)
                yield return Item(new StatusEvent($"{id}:resync:{log.Cursor(id)}", id, run ?? "", EventTypes.Resync,
                    StatusEvent.SchemaVersion, log.Cursor(id) + 1, Reason: "client_queue_full"));
        }
        finally
        {
            sub.Close();
        }
    }

    private static bool IsRunStart(StatusEvent e) => e.Type == EventTypes.State && e.State == OperationState.Running;

    // The SSE id is the cursor, so a reconnecting EventSource sends it back as Last-Event-ID.
    // A resync event has no id: it must not move the client's resume point.
    private static SseItem<StatusEvent> Item(StatusEvent e) =>
        e.Type == EventTypes.Resync
            ? new SseItem<StatusEvent>(e, e.Type)
            : new SseItem<StatusEvent>(e, e.Type) { EventId = e.Cursor.ToString(CultureInfo.InvariantCulture) };
}

/// <summary>A C# client for the stream: reads typed events with SseParser.</summary>
public static class StatusStreamClient
{
    public static async IAsyncEnumerable<StatusEvent> ReadAsync(HttpClient http, string url, long after,
        string? bearer = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (after > 0) request.Headers.Add("Last-Event-ID", after.ToString(CultureInfo.InvariantCulture));
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using Stream body = await response.Content.ReadAsStreamAsync(ct);
        SseParser<StatusEvent> parser = SseParser.Create(body, (type, data) =>
            JsonSerializer.Deserialize<StatusEvent>(data, JsonSerializerOptions.Web)!);
        await foreach (SseItem<StatusEvent> item in parser.EnumerateAsync(ct))
            yield return item.Data;
    }
}
