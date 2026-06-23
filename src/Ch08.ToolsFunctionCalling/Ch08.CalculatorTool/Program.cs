// ============================================================================
// Chapter 8 — Tools and Function Calling (Calculator Tool)
// Demonstrates multiple tools and tool chaining: the agent calls several
// tools in sequence to answer a multi-step question like
// "What's 15% tip on a $47.50 bill in euros?"
//
// Key concepts:
//   - Multiple AIFunctionFactory.Create() registrations
//   - Static tool methods with [Description] on method + parameters
//   - Agent chaining tool calls for multi-step reasoning
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch08.CalculatorTool;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 8: Calculator Tool — Multi-Tool Chaining Demo ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Register all calculator tools
// ---------------------------------------------------------------
// Each static method becomes a separate tool the LLM can invoke.
AIFunction addTool = AIFunctionFactory.Create(CalculatorTools.Add);
AIFunction subtractTool = AIFunctionFactory.Create(CalculatorTools.Subtract);
AIFunction multiplyTool = AIFunctionFactory.Create(CalculatorTools.Multiply);
AIFunction divideTool = AIFunctionFactory.Create(CalculatorTools.Divide);
AIFunction convertTool = AIFunctionFactory.Create(CalculatorTools.ConvertUnits);

Console.WriteLine("Registered tools:");
Console.WriteLine($"  - {addTool.Name}: {addTool.Description}");
Console.WriteLine($"  - {subtractTool.Name}: {subtractTool.Description}");
Console.WriteLine($"  - {multiplyTool.Name}: {multiplyTool.Description}");
Console.WriteLine($"  - {divideTool.Name}: {divideTool.Description}");
Console.WriteLine($"  - {convertTool.Name}: {convertTool.Description}");
Console.WriteLine();

ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a precise math assistant. Use the provided calculator tools to solve problems step by step. Always show your work.",
    name: "CalculatorAssistant",
    description: "A math-savvy assistant with calculator and unit-conversion tools",
    tools: [addTool, subtractTool, multiplyTool, divideTool, convertTool]);

// ---------------------------------------------------------------
// Step 2: Multi-step problems requiring tool chaining
// ---------------------------------------------------------------
string[] problems =
[
    "What's 15% tip on a $47.50 bill in euros?",
    "If I run 5 km every day for a week, how many miles is that total?",
    "Split a $120 bill evenly among 4 people, then convert each share to GBP.",
    "What's the area of a rectangle that is 12.5 meters by 8.3 meters?"
];

foreach (string problem in problems)
{
    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    Console.WriteLine($"You: {problem}");

    try
    {
        AgentResponse response = await agent.RunAsync(
            problem,
            session,
            cancellationToken: cts.Token);

        Console.WriteLine($"Agent: {response.Text}\n");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled by user]");
        return;
    }
}

Console.WriteLine("=== Calculator Tool demo complete! ===");
