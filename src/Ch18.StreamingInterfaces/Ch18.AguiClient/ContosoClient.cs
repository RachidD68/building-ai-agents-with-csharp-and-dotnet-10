using System.Net.Http.Json;
using System.Text.Json;
using Ch18.SseStatusStream;

namespace Ch18.AguiClient;

/// <summary>
/// The console UI: the response over AG-UI, the operation over the status
/// stream. It prints "done" only when the status view holds a terminal state.
/// </summary>
public sealed class ContosoClient(HttpClient http, string token)
{
    public StatusView Status { get; private set; } = new();

    /// <summary>One AG-UI run. <paramref name="cutAfter"/> drops the connection after that many events.</summary>
    public async Task<AguiRunView> RunAsync(string path, string thread, string message, int? cutAfter = null,
        Action<JsonElement>? onEvent = null)
    {
        AguiRunView view = new("run_" + Guid.NewGuid().ToString("N")[..10]);
        using CancellationTokenSource cut = new();
        int seen = 0;
        try
        {
            await foreach (JsonElement e in AguiStream.RunAsync(http, path, token, thread, view.RunId, message, cut.Token))
            {
                view.Apply(e);
                onEvent?.Invoke(e);
                if (++seen == cutAfter) await cut.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (cut.IsCancellationRequested) { }
        view.EndOfStream();
        return view;
    }

    /// <summary>The operation as the server has it now: a snapshot replaces the view. Null: not found (404).</summary>
    public async Task<StatusView?> ResyncAsync(string thread)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"/conversations/{thread}/snapshot");
        request.Headers.Authorization = new("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        StatusView view = new();
        view.Apply((await response.Content.ReadFromJsonAsync<StatusEvent>(JsonSerializerOptions.Web))!);
        return Status = view;
    }

    /// <summary>Follows the status stream from the view's cursor until a terminal state or the timeout.</summary>
    public async Task FollowAsync(string thread, TimeSpan timeout, Action<StatusEvent>? onEvent = null)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            await foreach (StatusEvent e in StatusStreamClient.ReadAsync(http, $"/conversations/{thread}/events",
                Status.Cursor, token, cts.Token))
            {
                Status.Apply(e);
                onEvent?.Invoke(e);
                if (Status.ShowsDone) break;
            }
        }
        catch (OperationCanceledException) { }
    }

    public string Screen(AguiRunView run) =>
        $"response: {(run.NeedsResync ? "cut, discarded" : run.Finished ? "finished" : run.Error ?? "open")}" +
        $" | operation: {Status.State?.ToString() ?? "none"} ({Status.Reason})" +
        $" | {(Status.ShowsDone ? "DONE" : "not done")}";
}
