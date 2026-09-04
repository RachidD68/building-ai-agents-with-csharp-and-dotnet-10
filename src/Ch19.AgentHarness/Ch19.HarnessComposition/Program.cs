// ============================================================================
// Chapter 19 — Composing the harness: instructions, switches, your own tools
// ----------------------------------------------------------------------------
// The harness is opinionated, not closed. This project shows the three ways
// you shape it:
//
//   1. Instruction layers. DefaultInstructions (the harness's own, a public
//      const you can read) -> HarnessInstructions (yours, about the job) ->
//      ChatOptions.Instructions (yours, about the voice). Setting
//      HarnessInstructions to "" omits that layer entirely.
//   2. Opt-out switches. Every default is a Disable* flag. Turning one off
//      removes a capability AND the tools it injected, which is how you keep
//      the prompt small on a local model.
//   3. Your own tools and providers. ChatOptions.Tools and AIContextProviders
//      compose with the harness's, they do not replace them.
//
// The demo builds two agents from the same chat client -- a full one and a
// stripped one -- and prints the tool surface each ends up advertising.
// ============================================================================

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration,
    attemptTimeout: TimeSpan.FromMinutes(8));

using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 19: Composing the harness ===");
Console.WriteLine();

// ── 1. The instruction layers ─────────────────────────────────────────
// HarnessAgent.DefaultInstructions is a public const, so you can read what
// the harness tells the model before you add anything.
string harnessDefault = HarnessAgent.DefaultInstructions;
Console.WriteLine("Layer 1 — HarnessAgent.DefaultInstructions (shipped with the harness):");
Console.WriteLine($"  {harnessDefault.Length} characters, first line:");
Console.WriteLine($"  \"{harnessDefault.Split('\n')[0].Trim()}\"");
Console.WriteLine();
Console.WriteLine("Layer 2 — HarnessInstructions  : what this deployment is for");
Console.WriteLine("Layer 3 — ChatOptions.Instructions : how it should sound");
Console.WriteLine();

// ── 2. Your own tool, composed with the harness's ─────────────────────
[Description("Looks up the shipping status for a Contoso order id.")]
static string CheckShipping(
    [Description("The Contoso order id, for example ORD-3003.")] string orderId) =>
    orderId.EndsWith("3", StringComparison.Ordinal)
        ? $"{orderId}: in transit, due Thursday."
        : $"{orderId}: delivered.";

AIFunction shippingTool = AIFunctionFactory.Create(CheckShipping);

// ── 3. Two agents, same client, different surface ─────────────────────
HarnessAgentOptions full = new()
{
    Name = "FullHarness",
    HarnessInstructions = "You work Contoso's support desk. Use CheckShipping for delivery questions.",
    ChatOptions = new ChatOptions { Instructions = "Be brief.", Tools = [shippingTool] },
    DisableWebSearch = true,      // local provider cannot serve hosted search
    DisableFileMemory = true,     // leave nothing on disk
};

HarnessAgentOptions minimal = new()
{
    Name = "MinimalHarness",
    // Setting this to the empty string omits the layer entirely -- the model
    // sees the harness default plus your ChatOptions.Instructions, nothing else.
    HarnessInstructions = "",
    ChatOptions = new ChatOptions { Instructions = "Be brief.", Tools = [shippingTool] },
    // Strip the harness back to a plain tool-calling agent. Each flag removes
    // both a capability and the tools it would have injected.
    DisableTodoProvider = true,
    DisableAgentModeProvider = true,
    DisableAgentSkillsProvider = true,
    DisableWebSearch = true,
    DisableFileMemory = true,
    // Keep approvals on: a smaller agent is not a less careful one.
    DisableToolAutoApproval = false,
    // Bound the tool loop explicitly rather than relying on the default.
    MaximumIterationsPerRequest = 5,
};

HarnessAgent fullAgent = chatClient.AsHarnessAgent(full);
HarnessAgent minimalAgent = chatClient.AsHarnessAgent(minimal);

Console.WriteLine($"{fullAgent.Name,-16} : todos ON, modes ON, skills ON  + your CheckShipping tool");
Console.WriteLine($"{minimalAgent.Name,-16} : todos off, modes off, skills off + your CheckShipping tool");
Console.WriteLine($"{"",-16}   MaximumIterationsPerRequest = {minimal.MaximumIterationsPerRequest}");
Console.WriteLine();

// ── Same question, both agents ────────────────────────────────────────
const string question = "Is order ORD-3003 going to arrive this week?";

foreach ((string label, HarnessAgent agent) in
         new[] { ("FullHarness", fullAgent), ("MinimalHarness", minimalAgent) })
{
    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"You: {question}");
    Console.Write("Agent: ");
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    await foreach (AgentResponseUpdate update in
        agent.RunStreamingAsync(question, session, cancellationToken: cts.Token))
    {
        Console.Write(update.Text);
    }
    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("Both answered the same question. The full harness carried planning");
Console.WriteLine("machinery it did not need here; the minimal one is what you ship when");
Console.WriteLine("the job is narrow and the prompt budget is tight.");
Console.WriteLine();
Console.WriteLine("=== Harness composition demo complete! ===");
