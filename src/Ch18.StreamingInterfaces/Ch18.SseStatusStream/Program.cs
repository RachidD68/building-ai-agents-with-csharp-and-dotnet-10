// ============================================================================
// Ch18.SseStatusStream: the operation's state is not the response's text.
// ----------------------------------------------------------------------------
//   GET /conversations/{id}/events     typed SSE: delta, tool, response.done,
//                                      state, cancel.requested, snapshot, resync
//                                      (id: = cursor; resume with Last-Event-ID)
//   GET /conversations/{id}/snapshot   the whole view at the current cursor
//
// The run streams text; the application decides the operation's state
// (AwaitingApproval for 120 EUR > the 50 EUR review limit, Completed only
// after a commit it observed). A client shows "done" only for a terminal state.
//
//   dotnet run                 the live run: Ollama (LlmProvider), in-process
//                              server, a client that drops mid-stream and
//                              resumes from its cursor
//   dotnet run -- --self-test  offline: scripted model, every rule and the
//                              failures they prevent (RESULT n/n)
// Simulated effects only: nothing is paid.
// ============================================================================

using AgenticAI.Common.Extensions;
using Ch18.SseStatusStream;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;

if (args.Contains("--self-test")) return await SseSelfTest.RunAsync();

Console.WriteLine("=== Ch18.SseStatusStream: live run (in-process server, LlmProvider model) ===\n");
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseTestServer();
builder.Logging.ClearProviders();
builder.Services.AddLlmChatClient(builder.Configuration);
OperationLog log = new();
await using WebApplication app = builder.Build();
app.MapStatusStream("/conversations", log, (_, _) => true);   // ownership: see Contoso v6
await app.StartAsync();
HttpClient http = app.GetTestClient();

AIAgent agent = app.Services.GetRequiredService<IChatClient>().AsAIAgent(
    instructions: "You are Contoso's expense assistant. Call get_review_limit before you answer. Answer in two sentences.",
    tools: [AIFunctionFactory.Create(() => "Claims above 50 EUR wait for a finance reviewer.", "get_review_limit")]);
const string conversation = "c-live";
string runId = log.StartRun(conversation);
Task<string> run = StatusRunner.StreamResponseAsync(log, conversation, runId, agent, await agent.CreateSessionAsync(),
    "I am submitting a taxi receipt of 120 EUR. What happens next?");

StatusView view = new();
Console.WriteLine("-- client connects at cursor 0, drops the connection after 5 events");
using (CancellationTokenSource drop = new())
{
    int seen = 0;
    try
    {
        await foreach (StatusEvent e in StatusStreamClient.ReadAsync(http, $"/conversations/{conversation}/events", 0, ct: drop.Token))
        {
            view.Apply(e);
            Print(e);
            if (++seen == 5) await drop.CancelAsync();
        }
    }
    catch (OperationCanceledException) { }
}
Console.WriteLine($"   (disconnected) {view.Describe()}");
string reply = await run;   // the run went on while the client was away

// The application, not the model: 120 EUR is above the review limit.
log.SetState(conversation, runId, OperationState.AwaitingApproval, "amount_above_review_limit");
Console.WriteLine($"\n-- client reconnects with Last-Event-ID: {view.Cursor}");
using (CancellationTokenSource wait = new(TimeSpan.FromSeconds(5)))
{
    try
    {
        await foreach (StatusEvent e in StatusStreamClient.ReadAsync(http, $"/conversations/{conversation}/events", view.Cursor, ct: wait.Token))
        {
            view.Apply(e);
            Print(e);
            if (e.Type == EventTypes.State && e.State == OperationState.AwaitingApproval) break;
        }
    }
    catch (OperationCanceledException) { }
}
StatusEvent snapshot = log.Snapshot(conversation);
Console.WriteLine($"   {view.Describe()}");
Console.WriteLine($"   text applied once: {view.Text == snapshot.Text} ({view.Text.Length} chars, server {snapshot.Text!.Length})");
Console.WriteLine($"   event ids applied: {view.AppliedIds.Count}, discarded: {view.Discarded}, server cursor: {snapshot.Cursor}");
Console.WriteLine($"   the model said: \"{reply.Trim()}\"");
Console.WriteLine($"   the UI shows done: {view.ShowsDone} (state {view.State}, set by the application)");
return 0;

static void Print(StatusEvent e) =>
    Console.WriteLine($"   #{e.Cursor,-3} {e.Type,-14} {(e.Type == EventTypes.Delta ? $"\"{e.Text}\"" : $"{e.State} {e.Text} {e.Reason}".Trim())}");
