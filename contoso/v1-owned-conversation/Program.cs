// ============================================================================
// Contoso v1: owned conversation
// ----------------------------------------------------------------------------
// Alice starts a taxi claim with the expense assistant and comes back the next
// day, after a restart, to finish it. Bob, in the same tenant, sends her
// conversation id with her tenant and subject written into the request, and
// gets nothing. Alice has the claim open in two windows and both send at once:
// one is answered, the other is told to reload. Then Alice asks the assistant
// to forget the conversation, twice, and after another restart it is gone,
// while Bob's own conversation is still there.
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     a scripted model, no network; RESULT n/n
// The session store is the conversations chapter's SqliteSessionStore
// (AgentSessionStore, experimental MAAI001), in a temporary folder.
// ============================================================================

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ch06.SessionOwnership;
using Contoso.OwnedConversation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

bool selfTest = args.Contains("--self-test");
IChatClient model;
IHost? host = null;
if (selfTest)
{
    model = new ScriptedExpenseModel();
}
else
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddLlmChatClient(builder.Configuration);
    host = builder.Build();
    model = host.Services.GetRequiredService<IChatClient>();
}

Console.WriteLine(selfTest ? "=== Contoso: owned conversation (self-test) ===" : "=== Contoso: owned conversation ===");
string root = Path.Combine(Path.GetTempPath(), "contoso-v1-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    return await Scenario.RunAsync(model, Path.Combine(root, "conversations.db"), selfTest);
}
finally
{
    host?.Dispose();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(root, recursive: true); } catch (IOException) { }
}

internal static class Scenario
{
    const string Tenant = "contoso";
    static int s_passed, s_total;
    static bool s_assert;

    public static async Task<int> RunAsync(IChatClient model, string db, bool assert)
    {
        s_assert = assert;
        var directory = new EmployeeDirectory();
        directory.Add(Tenant, "E-1001");
        directory.Add(Tenant, "E-2002");
        AuthenticatedCaller alice = directory.Authenticate(Tenant, "E-1001");
        AuthenticatedCaller bob = directory.Authenticate(Tenant, "E-2002");
        string claim = "claim-" + Guid.Parse("4b9e2c1d-7a3f-4e8b-9c6d-1f2a3b4c5d6e").ToString("N")[..8];
        const string Question = "Which amount did I give you for the taxi?";

        Console.WriteLine("\n-- 1. Day one: Alice starts the taxi claim --");
        var desk = new ExpenseConversations(model, db);
        await Say(desk, alice, new(claim,
            "Taxi from the airport to the Fabrikam office on 12 November, receipt attached: 64.80 EUR."));

        Console.WriteLine("\n-- 2. Day two, after a restart: Alice asks what she said --");
        desk = new ExpenseConversations(model, db);
        TurnResult recall = await Say(desk, alice, new(claim, Question));
        Expect("the restored conversation answers from yesterday's message (64.80)",
            recall.Outcome == TurnOutcome.Answered && recall.Text.Contains("64.80"));

        Console.WriteLine("\n-- 3. Bob sends Alice's conversation id, with her tenant and subject in the request --");
        TurnResult probe = await Say(desk, bob, new(claim, Question, TenantId: Tenant, SubjectId: "E-1001"));
        Expect("Bob's turn ran on a new, empty conversation of his own: no 64.80 in the answer",
            !probe.Text.Contains("64.80") && await desk.MessageCountAsync(bob, claim) == 2);
        Expect("Alice's conversation is unchanged (4 messages)", await desk.MessageCountAsync(alice, claim) == 4);

        Console.WriteLine("\n-- 4. Alice has the claim open in two windows; both send at once --");
        TurnResult[] tabs = await Task.WhenAll(
            Task.Run(() => desk.SendAsync(alice, new(claim, "Window A: the trip purpose was a customer workshop."))),
            Task.Run(() => desk.SendAsync(alice, new(claim, "Window B: please also note the toll, 3.10 EUR."))));
        for (int i = 0; i < tabs.Length; i++) Console.WriteLine($"  window {(char)('A' + i)}: {tabs[i].Outcome}: {OneLine(tabs[i].Text)}");
        Expect("one window was answered and the other was told to reload",
            tabs.Count(t => t.Outcome == TurnOutcome.Answered) == 1 && tabs.Count(t => t.Outcome == TurnOutcome.Conflict) == 1);
        Expect("the stored conversation has the winning turn only (6 messages)",
            await desk.MessageCountAsync(alice, claim) == 6);

        Console.WriteLine("\n-- 5. Alice asks the assistant to forget the conversation, twice --");
        await desk.ForgetAsync(alice, claim);
        await desk.ForgetAsync(alice, claim);
        Console.WriteLine("  deleted; the second delete was a no-op");
        desk = new ExpenseConversations(model, db);
        Expect("after a restart Alice's conversation is gone", await desk.MessageCountAsync(alice, claim) == 0);
        Expect("Bob's own conversation with the same id survives", await desk.MessageCountAsync(bob, claim) == 2);
        TurnResult after = await Say(desk, alice, new(claim, Question));
        Expect("asked again, the assistant no longer knows the amount", !after.Text.Contains("64.80"));

        if (assert) Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return !assert || s_passed == s_total ? 0 : 1;
    }

    static async Task<TurnResult> Say(ExpenseConversations desk, AuthenticatedCaller who, ChatRequest request)
    {
        Console.WriteLine($"  {who.SubjectId}: {request.Text}");
        TurnResult result = await desk.SendAsync(who, request);
        Console.WriteLine($"  assistant ({result.Outcome}): {OneLine(result.Text)}");
        return result;
    }

    static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    // Against a real model the checks are printed, not enforced: its wording varies.
    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine(s_assert ? $"  {(ok ? "PASS" : "FAIL")}  {name}" : $"  [{(ok ? "as expected" : "differs")}] {name}");
    }
}

/// <summary>
/// The self-test's model. It answers a question about an amount from the
/// earlier user messages it was sent, which is what makes a lost or leaked
/// history visible; anything else is acknowledged.
/// </summary>
internal sealed partial class ScriptedExpenseModel : IChatClient
{
    // Both windows' calls wait here until the other has arrived, so both
    // windows loaded the same version before either saves.
    private readonly CountdownEvent _windows = new(2);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> users = [.. messages.Where(m => m.Role == ChatRole.User)];
        string last = users.Count == 0 ? "" : users[^1].Text;
        if (last.StartsWith("Window", StringComparison.Ordinal))
        {
            _windows.Signal();
            if (!_windows.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                throw new TimeoutException("the second window never arrived");
        }
        string answer;
        if (last.Contains("Which amount"))
        {
            Match? found = users[..^1].Select(m => Amount().Match(m.Text)).FirstOrDefault(m => m.Success);
            answer = found is null
                ? "I have no earlier message about a taxi in this conversation."
                : $"You told me the taxi cost {found.Value}.";
        }
        else
        {
            answer = "Noted.";
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    [GeneratedRegex(@"\d+\.\d{2} EUR")]
    private static partial Regex Amount();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() => _windows.Dispose();
}
