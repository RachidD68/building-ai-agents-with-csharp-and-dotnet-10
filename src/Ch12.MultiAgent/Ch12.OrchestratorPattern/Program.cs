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
//   User Query → Orchestrator → [CodeExpert | DataExpert | SecurityExpert]
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
    instructions: """
        You are a senior software engineering expert.
        Provide clear, concise technical advice about software architecture,
        design patterns, code quality, and programming best practices.
        Keep your response to 2-3 sentences.
        """,
    name: "CodeExpert",
    description: "Expert in software engineering, architecture, and code quality");

ChatClientAgent dataExpert = new(
    chatClient,
    instructions: """
        You are a data engineering and analytics expert.
        Provide clear, concise technical advice about databases, data pipelines,
        ETL processes, data modeling, and analytics tools.
        Keep your response to 2-3 sentences.
        """,
    name: "DataExpert",
    description: "Expert in databases, data pipelines, and analytics");

ChatClientAgent securityExpert = new(
    chatClient,
    instructions: """
        You are a cybersecurity expert.
        Provide clear, concise technical advice about application security,
        threat modeling, authentication, authorization, and secure coding.
        Keep your response to 2-3 sentences.
        """,
    name: "SecurityExpert",
    description: "Expert in cybersecurity, authentication, and secure coding");

// ---------------------------------------------------------------
// Step 2: Wrap each specialist as a tool the orchestrator can call
// ---------------------------------------------------------------
// Each tool function invokes the specialist agent and returns its response.
// The orchestrator's LLM sees the tool descriptions and decides which to call.

AIFunction codeExpertTool = AIFunctionFactory.Create(
    [Description("Consult the code and architecture expert for questions about software design, patterns, code quality, and programming.")] async (
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
    [Description("Consult the data expert for questions about databases, data pipelines, ETL, data modeling, and analytics.")] async (
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
    [Description("Consult the security expert for questions about cybersecurity, authentication, authorization, and secure coding.")] async (
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
        You are a technical team lead who routes questions to the right specialist.
        You have access to three experts:
        - CodeExpert: software architecture, design patterns, code quality
        - DataExpert: databases, data pipelines, ETL, analytics
        - SecurityExpert: cybersecurity, authentication, secure coding

        For each user question, decide which expert(s) to consult by calling
        the appropriate tool. If a question spans multiple domains, consult
        multiple experts. Synthesize their answers into a coherent response.
        """,
    name: "Orchestrator",
    description: "Routes questions to the appropriate specialist agent",
    tools: [codeExpertTool, dataExpertTool, securityExpertTool]);

// ---------------------------------------------------------------
// Step 4: Send different queries to see routing in action
// ---------------------------------------------------------------
string[] queries =
[
    "What design pattern should I use for a plugin system?",
    "How should I design a data pipeline for real-time analytics?",
    "What are the OWASP top 10 risks I should address in my web app?",
    "How do I securely store API keys while keeping my code clean?"  // Multi-domain
];

foreach (string query in queries)
{
    Console.WriteLine($"User: {query}\n");

    try
    {
        AgentSession session = await orchestrator.CreateSessionAsync(cts.Token);
        AgentResponse response = await orchestrator.RunAsync(
            query, session, cancellationToken: cts.Token);

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
