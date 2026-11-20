// ============================================================================
// Exercise 02: Streaming Comparison (Intermediate)
// ----------------------------------------------------------------------------
// The same prompt through RunAsync and RunStreamingAsync: time to the first
// update and total time for each. A run that does not complete is reported by
// its state (cancelled, truncated, failed), never as a short success with a
// total time. Press Ctrl+C during the streamed run to see a cancelled one.
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     a fake IChatClient, no network; RESULT n/n
// ============================================================================

using System.Diagnostics;
using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Ch04.FirstAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
ChatClientAgent agent = new(host.Services.GetRequiredService<IChatClient>(),
    instructions: "You are a concise assistant for .NET developers.", name: "Comparer");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

const string Prompt = "Explain the SOLID principles in five short lines.";
Console.WriteLine("=== Streaming Comparison ===\n");
Timing whole = await Compare.NonStreamingAsync(agent, Prompt, cts.Token);
Console.WriteLine("--- Update arrival (streaming) ---");
Timing streamed = await Compare.StreamingAsync(agent, Prompt, cts.Token,
    (at, text) => Console.WriteLine($"  {at.TotalMilliseconds,7:F0} ms | {new string('#', Math.Min(40, (int)at.TotalMilliseconds / 100))} {text.ReplaceLineEndings(" ")}"));
Console.WriteLine("\n--- Summary ---");
Console.WriteLine("  " + Compare.Describe(whole));
Console.WriteLine("  " + Compare.Describe(streamed));
return 0;

static async Task<int> SelfTest()
{
    Console.WriteLine("=== Streaming Comparison (self-test) ===");
    int passed = 0, total = 0;
    void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

    FakeChatClient model = new(
        FakeReply.Text("S, O, L, I, D."),
        FakeReply.Stream(ChatFinishReason.Stop, "S, ", "O, ", "L, ", "I, ", "D."),
        FakeReply.Hang("S, "));
    ChatClientAgent agent = new(model, instructions: "test", name: "Comparer");

    Timing whole = await Compare.NonStreamingAsync(agent, "SOLID?", CancellationToken.None);
    Timing streamed = await Compare.StreamingAsync(agent, "SOLID?", CancellationToken.None);
    using CancellationTokenSource cts = new();
    Timing stopped = await Compare.StreamingAsync(agent, "SOLID?", cts.Token, (_, _) => cts.Cancel());
    foreach (Timing t in new[] { whole, streamed, stopped }) Console.WriteLine("   " + Compare.Describe(t));

    Expect("non-streaming: the first output is the whole answer", whole.FirstUpdate == whole.Total);
    Expect("streaming: the first update comes no later than the end",
        streamed.Status == RunStatus.Completed && streamed.FirstUpdate <= streamed.Total && streamed.Text == "S, O, L, I, D.");
    Expect("a cancelled run is reported as cancelled, with no total time",
        stopped.Status == RunStatus.Cancelled && Compare.Describe(stopped).Contains("cancelled")
        && !Compare.Describe(stopped).Contains("total"));

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

internal sealed record Timing(string Mode, RunStatus Status, TimeSpan? FirstUpdate, TimeSpan Total, string Text);

internal static class Compare
{
    public static async Task<Timing> NonStreamingAsync(AIAgent agent, string prompt, CancellationToken ct)
    {
        Stopwatch clock = Stopwatch.StartNew();
        RunOutcome outcome = await AgentRuns.RunAsync(() => agent.RunAsync(prompt, cancellationToken: ct), ct);
        TimeSpan total = clock.Elapsed;
        return new("non-streaming", outcome.Status, outcome.Text.Length > 0 ? total : null, total, outcome.Text);
    }

    public static async Task<Timing> StreamingAsync(AIAgent agent, string prompt, CancellationToken ct,
        Action<TimeSpan, string>? onUpdate = null)
    {
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan? first = null;
        RunOutcome outcome = await AgentRuns.CollectAsync(agent.RunStreamingAsync(prompt, cancellationToken: ct), ct,
            text => { first ??= clock.Elapsed; onUpdate?.Invoke(clock.Elapsed, text); });
        return new("streaming", outcome.Status, first, clock.Elapsed, outcome.Text);
    }

    public static string Describe(Timing t) => t.Status == RunStatus.Completed
        ? $"{t.Mode,-13} first output {t.FirstUpdate?.TotalMilliseconds:F0} ms, total {t.Total.TotalMilliseconds:F0} ms"
        : $"{t.Mode,-13} {t.Status.ToString().ToLowerInvariant()} after {t.Total.TotalMilliseconds:F0} ms " +
          $"({t.Text.Length} characters arrived, not an answer)";
}
