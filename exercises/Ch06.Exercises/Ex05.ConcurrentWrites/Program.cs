// ============================================================================
// Exercise 05 — Concurrent writes (Challenge)
// Two requests load the same conversation at version N and both save. Without
// a concurrency token the second save silently erases the first turn. With
// SqliteSessionStore one save wins and the other gets SessionConflictException;
// this solution then reloads the conversation, runs its turn again on the new
// version and saves, so both turns end up stored, each once.
//
//   dotnet run -- --self-test      prints RESULT n/n, exit code 1 on any failure
// No model: EchoChatClient answers offline. AgentSessionStore is MAAI001.
// ============================================================================

using System.Collections.Concurrent;
using Ch06.SessionOwnership;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Ex05 concurrent writes: self-test ==="
    : "=== Ex05 concurrent writes (no model; the same checks run with --self-test) ===");

string root = Path.Combine(Path.GetTempPath(), "ch06-ex05-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, total = 0;
try
{
    var history = new InMemoryChatHistoryProvider();
    var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions { ChatHistoryProvider = history });
    var store = new SqliteSessionStore(Path.Combine(root, "sessions.db"));
    AgentSessionStoreKey key = ConversationKeys.For(new AuthenticatedCaller("contoso", "E-1001"), "claim-hotel");

    AgentSession first = await store.GetOrCreateSessionAsync(agent, key);
    await agent.RunAsync("Hotel, two nights in Lyon.", first);
    await store.SaveSessionAsync(agent, key, first);

    int rounds = 10, overlapped = 0, conflicts = 0, retried = 0;
    List<string> sent = [];
    for (int round = 1; round <= rounds; round++)
    {
        // Both writers load the same version before either saves.
        string[] lines = [$"window A, round {round}", $"window B, round {round}"];
        AgentSession[] loaded = [(await store.GetSessionAsync(agent, key))!, (await store.GetSessionAsync(agent, key))!];
        int inFlight = 0, maxInFlight = 0;
        ConcurrentBag<int> attemptsPerWriter = [];
        using var start = new ManualResetEventSlim(false);
        Task[] writers = [.. Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            start.Wait();
            int now = Interlocked.Increment(ref inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref maxInFlight)) &&
                   Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen) { }
            attemptsPerWriter.Add(await SaveTurnWithRetryAsync(loaded[i], lines[i]));
            Interlocked.Decrement(ref inFlight);
        }))];
        start.Set();
        await Task.WhenAll(writers);
        if (maxInFlight >= 2) overlapped++;
        conflicts += attemptsPerWriter.Sum(a => a - 1);
        if (attemptsPerWriter.Order().SequenceEqual([1, 2])) retried++;
        sent.AddRange(lines);
    }

    List<ChatMessage> stored = history.GetMessages((await store.GetSessionAsync(agent, key))!);
    string[] userLines = [.. stored.Where(m => m.Role == ChatRole.User).Select(m => m.Text)];
    Console.WriteLine($"  {rounds} rounds, {conflicts} conflicts, {stored.Count} messages stored");
    Expect("both writers were in flight together in at least one round", overlapped >= 1);
    Expect("every round: one save won at once, the other hit one conflict and won on its retry", retried == rounds);
    Expect("no turn was lost: every line sent is stored", sent.All(userLines.Contains));
    Expect("no turn was stored twice", userLines.Length == userLines.Distinct().Count() && userLines.Length == 1 + 2 * rounds);

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;

    // Run the turn on the loaded copy and save; on a conflict, reload the
    // conversation (which now holds the other writer's turn) and run the turn
    // again on that version. Returns the number of save attempts.
    async Task<int> SaveTurnWithRetryAsync(AgentSession session, string line)
    {
        for (int attempt = 1; ; attempt++)
        {
            await agent.RunAsync(line, session);
            try
            {
                await store.SaveSessionAsync(agent, key, session);
                return attempt;
            }
            catch (SessionConflictException) when (attempt < 3)
            {
                session = (await store.GetSessionAsync(agent, key))!;
            }
        }
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

void Expect(string name, bool ok)
{
    total++;
    if (ok) passed++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
}
