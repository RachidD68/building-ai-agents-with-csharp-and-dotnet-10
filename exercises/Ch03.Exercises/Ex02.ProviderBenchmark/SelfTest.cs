using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Ex02.ProviderBenchmark;

/// <summary>
/// The harness on a manual clock: the fake client moves the clock itself, so
/// every figure is exact and the test cannot be flaky.
/// </summary>
internal static class SelfTest
{
    static int s_passed, s_total;

    public static int Run()
    {
        Console.WriteLine("=== Provider benchmark: self-test (fake client, manual clock) ===\n");

        // 120 ms of thinking, then 3 chunks 30 ms apart: first token 120 ms, total 210 ms.
        ManualClock clock = new();
        using FakeStreamingClient steady = new(clock, firstMs: 120, chunkMs: 30, chunks: 3, failOnCall: null);
        IReadOnlyList<CallTiming> runs = Benchmark.RunAsync(steady, "q", 5, clock).GetAwaiter().GetResult();
        Expect("5 calls -> 5 timings", runs.Count == 5 && steady.Calls == 5);
        Expect("first token = 120 ms on every call", runs.All(r => r.FirstToken == TimeSpan.FromMilliseconds(120)));
        Expect("total = 210 ms on every call (first token is not the total)",
            runs.All(r => r.Total == TimeSpan.FromMilliseconds(210)));

        ManualClock clock2 = new();
        using FakeStreamingClient flaky = new(clock2, firstMs: 100, chunkMs: 50, chunks: 2, failOnCall: 3);
        BenchmarkSummary summary = BenchmarkSummary.From(Benchmark.RunAsync(flaky, "q", 5, clock2).GetAwaiter().GetResult());
        Expect("a failed call is counted apart: 4 OK, 1 failed", summary is { Succeeded: 4, Failed: 1 });
        Expect("averages use successes only: first token 100 ms, total 200 ms",
            summary.AvgFirstTokenMs == 100 && summary.AvgTotalMs == 200);

        BenchmarkSummary allFailed = BenchmarkSummary.From([new CallTiming(null, null, "HttpRequestException: refused")]);
        Expect("all calls failed -> no average is invented", allFailed is { Succeeded: 0, Failed: 1, AvgTotalMs: null });
        Expect("the table prints '-' where there is no figure",
            BenchmarkSummary.Table([("ollama (llama3.2)", allFailed)]).Contains("  0      1               -         -"));

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

/// <summary>A clock that moves only when told to.</summary>
internal sealed class ManualClock : TimeProvider
{
    long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(int milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
}

/// <summary>Streams a scripted answer and advances the manual clock as it goes.</summary>
internal sealed class FakeStreamingClient(ManualClock clock, int firstMs, int chunkMs, int chunks, int? failOnCall)
    : IChatClient
{
    public int Calls { get; private set; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Calls == failOnCall)
        {
            throw new HttpRequestException("simulated connection refused");
        }
        clock.Advance(firstMs);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk ");
        for (int i = 1; i < chunks; i++)
        {
            await Task.Yield();
            clock.Advance(chunkMs);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk ");
        }
        clock.Advance(chunkMs);   // the final update closes the stream
        yield return new ChatResponseUpdate(ChatRole.Assistant, "");
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The benchmark streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
