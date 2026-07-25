// ============================================================================
// Chapter 12 — Multi-Agent Systems (Orchestrator Pattern)
// Demonstrates the hub-and-spoke pattern: an orchestrator agent decides
// which specialist to call using function calling (agents-as-tools).
//
// Key concepts:
//   - Orchestrator agent with three specialist agents
//   - Agents-as-tools: each specialist is registered as an AIFunction
//   - The orchestrator's LLM decides which tool(s) to invoke
//   - Different queries route to different specialists automatically
//
// Architecture:
//   User Question → TechnicalOrchestrator → [CodeExpert | DataExpert | SecurityExpert]
// ============================================================================

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 12: Orchestrator Pattern — Hub-and-Spoke ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Create the specialist agents
// ---------------------------------------------------------------
ChatClientAgent codeExpert = new(
    chatClient,
    instructions: "You are a senior software architect. Answer questions about " +
                  "code patterns, architecture, and best practices. Be concise and practical.",
    name: "CodeExpert",
    description: "Expert on code, architecture, and software engineering patterns");

ChatClientAgent dataExpert = new(
    chatClient,
    instructions: "You are a senior data engineer. Answer questions about " +
                  "data pipelines, schemas, ETL, and analytics.",
    name: "DataExpert",
    description: "Expert on data engineering, pipelines, and analytics");

ChatClientAgent securityExpert = new(
    chatClient,
    instructions: "You are a senior security engineer. Answer questions about " +
                  "authentication, authorization, vulnerabilities, and threat modeling.",
    name: "SecurityExpert",
    description: "Expert on security, auth, and threat modeling");

// ---------------------------------------------------------------
// Step 2: Wrap each specialist as a tool the orchestrator can call
// ---------------------------------------------------------------
// Each tool function invokes the specialist agent and returns its response.
// The orchestrator's LLM sees the tool descriptions and decides which to call.

AIFunction codeExpertTool = AIFunctionFactory.Create(
    [Description("Consult the code and architecture expert for questions about software design, patterns, or implementation.")] async (
    [Description("The technical question to ask the code expert")] string question,
    CancellationToken cancellationToken) =>
    {
        AgentSession session = await codeExpert.CreateSessionAsync(cancellationToken);
        AgentResponse response = await codeExpert.RunAsync(
            question, session, cancellationToken: cancellationToken);
        return response.Text;
    },
    name: "AskCodeExpert");

AIFunction dataExpertTool = AIFunctionFactory.Create(
    [Description("Consult the data expert for questions about data pipelines, schemas, ETL, or analytics.")] async (
    [Description("The technical question to ask the data expert")] string question,
    CancellationToken cancellationToken) =>
    {
        AgentSession session = await dataExpert.CreateSessionAsync(cancellationToken);
        AgentResponse response = await dataExpert.RunAsync(
            question, session, cancellationToken: cancellationToken);
        return response.Text;
    },
    name: "AskDataExpert");

AIFunction securityExpertTool = AIFunctionFactory.Create(
    [Description("Consult the security expert for questions about authentication, authorization, vulnerabilities, or threat modeling.")] async (
    [Description("The technical question to ask the security expert")] string question,
    CancellationToken cancellationToken) =>
    {
        AgentSession session = await securityExpert.CreateSessionAsync(cancellationToken);
        AgentResponse response = await securityExpert.RunAsync(
            question, session, cancellationToken: cancellationToken);
        return response.Text;
    },
    name: "AskSecurityExpert");

// ---------------------------------------------------------------
// Step 3: Create the orchestrator agent with all specialist tools
// ---------------------------------------------------------------
ChatClientAgent orchestrator = new(
    chatClient,
    instructions: """
        You are a technical team lead. When a user asks a question, route it to the
        appropriate specialist using the tools provided. For multi-domain questions,
        consult multiple specialists and synthesize their answers into a unified response.
        Always attribute information to the specialist who provided it.
        """,
    name: "TechnicalOrchestrator",
    description: "Routes technical questions to specialist agents",
    tools: [codeExpertTool, dataExpertTool, securityExpertTool]);

// ---------------------------------------------------------------
// Step 4: Send different questions to see routing in action
// ---------------------------------------------------------------
string[] questions =
[
    "How should I structure a CQRS pattern for a microservice?",
    "Our ETL pipeline is running slow — what should I look at?",
    "Is JWT with short-lived tokens and refresh rotation secure enough for our public API?",
    "How do I design a data pipeline that's secure end-to-end?"  // multi-domain
];

foreach (string question in questions)
{
    Console.WriteLine($"You: {question}\n");

    try
    {
        AgentSession session = await orchestrator.CreateSessionAsync(cts.Token);
        AgentResponse response = await orchestrator.RunAsync(
            question, session, cancellationToken: cts.Token);

        Console.WriteLine($"Orchestrator: {response.Text}");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine();
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled by user]");
        return;
    }
}

Console.WriteLine("=== Orchestrator Pattern demo complete! ===");
