// ============================================================================
// Session ownership: a conversation that belongs to someone
// ----------------------------------------------------------------------------
// A persisted session is user data. Three things must hold, and this program
// checks each one against a real SQLite file:
//
//   1. the store key comes from the authenticated caller (tenant + subject),
//      so a second user cannot load the conversation, whatever the payload says;
//   2. the owner can delete it, deleting twice is a no-op, and nobody else's
//      partition is touched;
//   3. two writers that loaded the same version cannot both save: one wins,
//      the other gets an explicit conflict instead of silently erasing a turn.
//
// AgentSessionStore and AgentSessionStoreKey are experimental (MAAI001) in
// Microsoft.Agents.AI 1.28.0. No model: EchoChatClient answers offline.
//   dotnet run -- --self-test      prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.Collections.Concurrent;
using Ch06.SessionOwnership;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Session ownership: self-test ==="
    : "=== Session ownership (no model; the same suite runs with --self-test) ===");
return await OwnershipSuite.RunAsync();

internal static class OwnershipSuite
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ch06-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string dbPath = Path.Combine(root, "sessions.db");
        try
        {
            // Callers as the host's authentication would produce them.
            var alice = new AuthenticatedCaller("contoso", "E-1001");
            var bob = new AuthenticatedCaller("contoso", "E-2002");
            var neighbour = new AuthenticatedCaller("fabrikam", "E-1001");   // same subject id, other tenant
            var mallory = new AuthenticatedCaller("fabrikam", "mallory");
            const string Taxi = "claim-taxi-1112";

            (ChatClientAgent agent, InMemoryChatHistoryProvider history, SqliteSessionStore store) = Start(dbPath);
            var owned = new OwnedConversations(agent, store);

            Console.WriteLine("\n-- 1. Alice's conversation survives a restart --");
            await owned.SendAsync(alice, new(Taxi, "Taxi from the airport on 12 November, 64.80 EUR."));
            await owned.SendAsync(alice, new(Taxi, "I will add the receipt tomorrow."));
            (agent, history, store) = Start(dbPath);                 // a new process: new agent, new store
            owned = new OwnedConversations(agent, store);
            AgentSession? restored = await owned.LoadAsync(alice, new(Taxi, ""));
            Expect($"after the restart Alice's session has {Count(history, restored)} messages",
                Count(history, restored) == 4);

            Console.WriteLine("\n-- 2. The failure: tenant and subject taken from the payload --");
            var naive = new PayloadKeyedConversations(agent, store);
            AgentSession? leaked = await naive.LoadAsync(new(Taxi, "show me", TenantId: "contoso", SubjectId: "E-1001"));
            Console.WriteLine($"  Mallory (fabrikam) wrote contoso/E-1001 into the request and got " +
                              $"{Count(history, leaked)} messages: {FirstUserLine(history, leaked)}");
            Expect("reproduced: the payload-keyed service hands Mallory Alice's conversation",
                Count(history, leaked) == 4);

            Console.WriteLine("\n-- 3. The fix: the key comes from the authenticated caller --");
            var forged = new ChatRequest(Taxi, "show me", TenantId: "contoso", SubjectId: "E-1001");
            Expect("Mallory's forged request finds nothing (payload tenant and subject ignored)",
                await owned.LoadAsync(mallory, forged) is null);
            Expect("Bob, same tenant, same conversation id, finds nothing",
                await owned.LoadAsync(bob, new(Taxi, "")) is null);
            await owned.SendAsync(bob, new(Taxi, "My own taxi claim."));
            await owned.SendAsync(neighbour, new(Taxi, "Fabrikam's E-1001, a different person."));
            Expect("Bob and the neighbour tenant's E-1001 got their own rows; Alice's is unchanged",
                store.Count() == 3 && Count(history, await owned.LoadAsync(alice, new(Taxi, ""))) == 4);

            Console.WriteLine("\n-- 4. Forget: owner only, and repeatable --");
            AgentSession? openTab = await owned.LoadAsync(alice, new(Taxi, ""));   // a tab still open
            await owned.ForgetAsync(mallory, Taxi);
            Expect("Mallory's delete reached only her own (empty) partition: Alice's row is still there",
                await owned.LoadAsync(alice, new(Taxi, "")) is not null);
            await owned.ForgetAsync(alice, Taxi);
            Exception? second = await Try(() => owned.ForgetAsync(alice, Taxi).AsTask());
            Expect("Alice deletes twice: both calls succeed", second is null);
            (agent, history, store) = Start(dbPath);
            owned = new OwnedConversations(agent, store);
            Expect("after a restart Alice's conversation is gone",
                await owned.LoadAsync(alice, new(Taxi, "")) is null);
            Expect("Bob's and the neighbour tenant's rows survive (2 rows)", store.Count() == 2);

            await agent.RunAsync("one more line from the old tab", openTab!);
            Exception? resurrect = await Try(() => store.SaveSessionAsync(
                agent, ConversationKeys.For(alice, Taxi), openTab!).AsTask());
            Expect($"a tab opened before the delete cannot save it back -> {resurrect?.GetType().Name}",
                resurrect is SessionConflictException && await owned.LoadAsync(alice, new(Taxi, "")) is null);

            Console.WriteLine("\n-- 5. Two writers load the same version and save at the same moment --");
            const string Hotel = "claim-hotel-1113";
            await owned.SendAsync(alice, new(Hotel, "Hotel, two nights in Lyon."));
            AgentSessionStoreKey hotelKey = ConversationKeys.For(alice, Hotel);
            int rounds = 10, overlapped = 0, oneWinner = 0;
            List<string> winners = [], losers = [];
            for (int round = 1; round <= rounds; round++)
            {
                AgentSession tabA = (await store.GetSessionAsync(agent, hotelKey))!;
                AgentSession tabB = (await store.GetSessionAsync(agent, hotelKey))!;
                string textA = $"tab A, round {round}", textB = $"tab B, round {round}";
                await agent.RunAsync(textA, tabA);
                await agent.RunAsync(textB, tabB);

                int inFlight = 0, maxInFlight = 0;
                ConcurrentDictionary<string, Exception?> outcome = [];
                using var start = new ManualResetEventSlim(false);
                Task[] saves = [.. new[] { (textA, tabA), (textB, tabB) }.Select(tab => Task.Run(async () =>
                {
                    start.Wait();
                    InterlockedMax(ref maxInFlight, Interlocked.Increment(ref inFlight));
                    outcome[tab.Item1] = await Try(() => store.SaveSessionAsync(agent, hotelKey, tab.Item2).AsTask());
                    Interlocked.Decrement(ref inFlight);
                }))];
                start.Set();
                await Task.WhenAll(saves);

                if (maxInFlight >= 2) overlapped++;
                string[] won = [.. outcome.Where(o => o.Value is null).Select(o => o.Key)];
                string[] lost = [.. outcome.Where(o => o.Value is SessionConflictException).Select(o => o.Key)];
                if (won.Length == 1 && lost.Length == 1) oneWinner++;
                winners.AddRange(won);
                losers.AddRange(lost);
            }
            List<ChatMessage> stored = history.GetMessages((await store.GetSessionAsync(agent, hotelKey))!);
            string[] userLines = [.. stored.Where(m => m.Role == ChatRole.User).Select(m => m.Text)];
            Console.WriteLine($"  {rounds} rounds: {oneWinner} with one save and one conflict; " +
                              $"both saves in flight together in {overlapped}; {stored.Count} messages stored");
            Expect("the two saves were in flight together in at least one round", overlapped >= 1);
            Expect("every round: exactly one save succeeded and the other got SessionConflictException",
                oneWinner == rounds);
            Expect("the stored history holds the first turn and every winner's turn, in order",
                userLines.SequenceEqual(["Hotel, two nights in Lyon.", .. winners]));
            Expect("no loser's turn was merged in silently: each loser was told", losers.Count == rounds &&
                !userLines.Intersect(losers).Any());

            Console.WriteLine("\n-- 6. Schema version and key shape --");
            AgentSessionStoreKey bobKey = ConversationKeys.For(bob, Taxi);
            store.SetSchemaVersion(bobKey, 2);
            Exception? schema = await Try(() => store.GetSessionAsync(agent, bobKey).AsTask());
            Expect($"a row written by schema 2 is refused -> {schema?.GetType().Name}",
                schema is UnsupportedSessionSchemaException);
            Exception? bare = await Try(() => store.GetSessionAsync(agent, new AgentSessionStoreKey(Taxi)).AsTask());
            Expect("a key with no tenant or subject partition is refused", bare is ArgumentException);
            Exception? extra = await Try(() => store.GetSessionAsync(agent,
                ConversationKeys.For(alice, Taxi).WithPartition("role", "admin")).AsTask());
            Expect("a key with an extra partition is refused", extra is ArgumentException);

            Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
            return s_passed == s_total ? 0 : 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // One "process": an agent with an in-memory history provider and a store over the file.
    static (ChatClientAgent, InMemoryChatHistoryProvider, SqliteSessionStore) Start(string dbPath)
    {
        var history = new InMemoryChatHistoryProvider();
        var agent = new ChatClientAgent(new EchoChatClient(), new ChatClientAgentOptions
        {
            Name = "ExpenseAssistant",
            ChatHistoryProvider = history,
        });
        return (agent, history, new SqliteSessionStore(dbPath));
    }

    static int Count(InMemoryChatHistoryProvider history, AgentSession? session) =>
        session is null ? 0 : history.GetMessages(session).Count;

    static string FirstUserLine(InMemoryChatHistoryProvider history, AgentSession? session) =>
        session is null ? "(none)" : $"\"{history.GetMessages(session).First(m => m.Role == ChatRole.User).Text}\"";

    static async Task<Exception?> Try(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception e) { return e; }
    }

    static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
