// ============================================================================
// Chapter 12 — Testing multi-agent compositions (MAF 1.16)
// ----------------------------------------------------------------------------
// You can test a pipeline's WIRING without spending a single token. The trick
// is a fake IChatClient that returns canned responses, so the agents behave
// deterministically. Then you assert on routing decisions and exact output
// ("golden transcript") the same way you would any other unit under test.
//
// This is a plain console assertion harness — no xUnit, no mocking library —
// because the book's companion solution does not standardize on a test
// framework. Each Check(...) throws on failure; main prints a pass/fail tally
// and returns a non-zero exit code if anything failed, so it works in CI.
// ============================================================================

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

int passed = 0;
int failed = 0;

void Check(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}");
    }
}

Console.WriteLine("=== Chapter 12: Testing multi-agent compositions ===");
Console.WriteLine();

// ── Test 1: a canned-response agent returns exactly what we scripted ─
Console.WriteLine("Test 1: deterministic agent output");
{
    ChatClientAgent agent = new(
        new ScriptedChatClient(_ => "BILLING"),
        instructions: "ignored by the fake",
        name: "Classifier");

    AgentSession session = await agent.CreateSessionAsync();
    AgentResponse response = await agent.RunAsync("I was double charged", session);

    Check("classifier returns the scripted label", response.Text.Trim() == "BILLING");
}

// ── Test 2: routing — the fake routes by inspecting the input ────────
// A realistic "triage" stub: it reads the user's message and emits the label
// a real router *should* produce. We assert the routing decision per case.
Console.WriteLine();
Console.WriteLine("Test 2: routing decisions");
{
    static string Route(string text)
    {
        string t = text.ToLowerInvariant();
        if (t.Contains("refund") || t.Contains("charge")) return "BILLING";
        if (t.Contains("login") || t.Contains("error")) return "TECHNICAL";
        return "GENERAL";
    }

    ChatClientAgent triage = new(
        new ScriptedChatClient(messages => Route(LastUserText(messages))),
        instructions: "ignored",
        name: "Triage");

    async Task<string> Ask(string text)
    {
        AgentSession s = await triage.CreateSessionAsync();
        AgentResponse r = await triage.RunAsync(text, s);
        return r.Text.Trim();
    }

    Check("billing message routes to BILLING", await Ask("I want a refund for a double charge") == "BILLING");
    Check("technical message routes to TECHNICAL", await Ask("I get an error on login") == "TECHNICAL");
    Check("other message routes to GENERAL", await Ask("What are your office hours?") == "GENERAL");
}

// ── Test 3: golden transcript — a two-stage pipeline end to end ──────
// Stage 1 (Summarizer) and stage 2 (Formatter) each have a scripted client.
// We assert the final text equals the exact expected transcript.
Console.WriteLine();
Console.WriteLine("Test 3: golden-transcript pipeline");
{
    ChatClientAgent summarizer = new(
        new ScriptedChatClient(_ => "Three reasons X beats Y."),
        instructions: "ignored",
        name: "Summarizer");

    ChatClientAgent formatter = new(
        new ScriptedChatClient(messages => $"# Summary\n{LastUserText(messages)}"),
        instructions: "ignored",
        name: "Formatter");

    AgentSession s1 = await summarizer.CreateSessionAsync();
    AgentResponse summary = await summarizer.RunAsync("long input document", s1);

    AgentSession s2 = await formatter.CreateSessionAsync();
    AgentResponse formatted = await formatter.RunAsync(summary.Text, s2);

    const string expected = "# Summary\nThree reasons X beats Y.";
    Check("pipeline produces the golden transcript", formatted.Text == expected);
}

// ── Summary / exit code ─────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"=== {passed} passed, {failed} failed ===");
return failed == 0 ? 0 : 1;

// ── Helpers ─────────────────────────────────────────────────────────
static string LastUserText(IEnumerable<ChatMessage> messages)
{
    string text = "";
    foreach (ChatMessage message in messages)
    {
        if (message.Role == ChatRole.User)
        {
            text = message.Text;
        }
    }
    return text;
}

// ============================================================================
// ScriptedChatClient — a fake IChatClient for deterministic, token-free tests.
// It ignores the model entirely and returns whatever the supplied function
// produces for the incoming messages. Implements both the blocking and the
// streaming paths so it works with RunAsync and RunStreamingAsync alike.
// ============================================================================
sealed class ScriptedChatClient(Func<IEnumerable<ChatMessage>, string> script) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string reply = script(messages);
        ChatResponse response = new(new ChatMessage(ChatRole.Assistant, reply));
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string reply = script(messages);
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
