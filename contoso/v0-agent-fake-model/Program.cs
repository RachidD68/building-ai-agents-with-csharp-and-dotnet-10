// ============================================================================
// Contoso v0: agent and fake model
// ----------------------------------------------------------------------------
// The Contoso expense assistant at its first milestone: it answers questions
// about the expense policy and does nothing else. Each answer is streamed and
// ends in one terminal state (completed, cancelled, truncated, failed).
// The self-test proves the messages and the cancellation path with a fake
// IChatClient; it says nothing about the quality of the answers.
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     a fake model, no network; RESULT n/n
// ============================================================================

using AgenticAI.Common.Extensions;
using AgenticAI.Testing;
using Ch04.FirstAgent;
using Contoso.AgentFakeModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
PolicyDesk desk = new(host.Services.GetRequiredService<IChatClient>());

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Contoso: agent and fake model ===\n");
string[] questions =
[
    "Is a 42 EUR taxi receipt approved automatically?",
    "Who approves a 180 EUR hotel claim?",
    "Can you pay my claim now?",
];
foreach (string question in questions)
{
    Console.WriteLine($"Employee: {question}");
    Console.Write($"{desk.Name}: ");
    RunOutcome outcome = await desk.AskAsync(question, cts.Token, Console.Write);
    Console.WriteLine($"\n  [{outcome.Status.ToString().ToLowerInvariant()}]\n");
    if (outcome.Status == RunStatus.Cancelled) return 130;
}
return 0;

static async Task<int> SelfTest()
{
    Console.WriteLine("=== Contoso: agent and fake model (self-test) ===");
    int passed = 0, total = 0;
    void Expect(string name, bool ok) { total++; if (ok) passed++; Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}"); }

    HttpRequestException outage = new("simulated outage: the model endpoint did not answer");
    FakeChatClient model = new(
        FakeReply.Stream(ChatFinishReason.Stop, "Yes: ", "42 EUR is under the 50.00 EUR limit."),
        FakeReply.Stream(ChatFinishReason.Stop, "A finance reviewer ", "who did not file it."),
        FakeReply.Hang("A claim above "),
        FakeReply.Throw(outage),
        FakeReply.Stream(ChatFinishReason.Length, "Claims are "));
    PolicyDesk desk = new(model);

    RunOutcome taxi = await desk.AskAsync("Is a 42 EUR taxi receipt approved automatically?", CancellationToken.None);
    RunOutcome hotel = await desk.AskAsync("Who approves a 180 EUR hotel claim?", CancellationToken.None);
    Expect("the policy instructions and the question reach the model",
        model.Calls[0].Options?.Instructions == ContosoPolicy.Instructions
        && model.Calls[0].Messages[^1].Text == "Is a 42 EUR taxi receipt approved automatically?");
    Expect("answers are Completed with the streamed text",
        taxi is { Status: RunStatus.Completed, Text: "Yes: 42 EUR is under the 50.00 EUR limit." }
        && hotel.Status == RunStatus.Completed);
    Expect("single-turn: the second question carries no history",
        model.Calls[1].Messages.Count(m => m.Role != ChatRole.System) == 1);

    using (CancellationTokenSource cts = new())
    {
        RunOutcome stopped = await desk.AskAsync("Explain the review rule.", cts.Token, _ => cts.Cancel());
        Expect("Stop after the first chunk is Cancelled and the model call saw the token",
            stopped is { Status: RunStatus.Cancelled, Text: "A claim above " } && model.Calls[2].SawCancellation);
    }

    RunOutcome down = await desk.AskAsync("Is parking reimbursed?", CancellationToken.None);
    Expect("a model outage is Failed and keeps the exception", down.Status == RunStatus.Failed && ReferenceEquals(down.Error, outage));
    RunOutcome cut = await desk.AskAsync("Explain the whole policy.", CancellationToken.None);
    Expect("a cut-off answer is Truncated, not Completed", cut.Status == RunStatus.Truncated);

    int calls = model.Calls.Count;
    bool refused = false;
    try
    {
        await desk.AskAsync(" ", CancellationToken.None);
    }
    catch (ArgumentException)
    {
        refused = true;
    }
    Expect("an empty question is refused before any model call", refused && model.Calls.Count == calls);

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}
