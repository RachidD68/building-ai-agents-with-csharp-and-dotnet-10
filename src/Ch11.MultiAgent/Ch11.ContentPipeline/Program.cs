// ============================================================================
// Chapter 11 — Multi-Agent Systems (Content Pipeline — Capstone)
// Demonstrates a three-stage content creation pipeline:
//   Researcher → Writer → Editor
//
// Key concepts:
//   - Three agents with distinct personas and responsibilities
//   - Researcher agent with a simulated search tool
//   - Writer agent with a creative persona
//   - Editor agent with a critical persona
//   - Full output at each stage shows how personas shape behavior
//
// This is the capstone for Chapter 11, combining multi-agent chaining
// with tool usage and persona-driven instructions.
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

Console.WriteLine("=== Chapter 11: Content Pipeline — Researcher → Writer → Editor ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ---------------------------------------------------------------
// Step 1: Define the simulated search tool
// ---------------------------------------------------------------
AIFunction searchTool = AIFunctionFactory.Create(SearchAsync);

// ---------------------------------------------------------------
// Step 2: Create the three pipeline agents
// ---------------------------------------------------------------
ChatClientAgent researcher = new(
    chatClient,
    instructions: """
        You are a thorough research assistant. When given a topic, use the
        search tool to gather relevant information. Compile your findings
        into a structured research brief with key facts, statistics, and
        notable points. Include source references where applicable.
        Output only the research brief.
        """,
    name: "Researcher",
    description: "Researches a topic using search tools and produces a structured brief",
    tools: [searchTool]);

ChatClientAgent writer = new(
    chatClient,
    instructions: """
        You are a creative and engaging tech writer with a warm, accessible style.
        Given a research brief, write a compelling short article (3-4 paragraphs)
        that makes the topic interesting and understandable for a general audience.
        Use vivid language and relatable analogies. Include a catchy title.
        Output only the article.
        """,
    name: "Writer",
    description: "Writes engaging articles from research briefs");

ChatClientAgent editor = new(
    chatClient,
    instructions: """
        You are a meticulous senior editor with high standards. Review the
        provided article for:
        - Clarity and readability
        - Factual accuracy based on the content
        - Grammar and style
        - Logical flow and structure

        Provide specific feedback with line-level suggestions, then output
        a final edited version of the article with your improvements applied.
        Format your response as:
        ## Editorial Feedback
        [your feedback points]

        ## Final Version
        [the improved article]
        """,
    name: "Editor",
    description: "Reviews and improves written content with editorial feedback");

// ---------------------------------------------------------------
// Step 3: Run the three-stage pipeline
// ---------------------------------------------------------------
string topic = "How agentic AI is transforming software development in 2025";

Console.WriteLine($"[TOPIC] {topic}\n");

// Stage 1: Research
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("STAGE 1: RESEARCH");
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine();

AgentSession researchSession = await researcher.CreateSessionAsync(cts.Token);
AgentResponse researchResult = await researcher.RunAsync(
    $"Research the following topic thoroughly: {topic}",
    researchSession,
    cancellationToken: cts.Token);

string researchBrief = researchResult.Text;
Console.WriteLine(researchBrief);
Console.WriteLine();

// Stage 2: Writing
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("STAGE 2: WRITING");
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine();

AgentSession writerSession = await writer.CreateSessionAsync(cts.Token);
AgentResponse writerResult = await writer.RunAsync(
    $"Write an engaging article based on this research brief:\n\n{researchBrief}",
    writerSession,
    cancellationToken: cts.Token);

string draftArticle = writerResult.Text;
Console.WriteLine(draftArticle);
Console.WriteLine();

// Stage 3: Editing
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine("STAGE 3: EDITING");
Console.WriteLine("=".PadRight(60, '='));
Console.WriteLine();

AgentSession editorSession = await editor.CreateSessionAsync(cts.Token);
AgentResponse editorResult = await editor.RunAsync(
    $"Review and improve this article:\n\n{draftArticle}",
    editorSession,
    cancellationToken: cts.Token);

Console.WriteLine(editorResult.Text);
Console.WriteLine();

Console.WriteLine("=== Content Pipeline complete! ===");

// ---------------------------------------------------------------
// Simulated Search Tool
// ---------------------------------------------------------------

/// <summary>
/// Simulates a web search, returning pre-defined results about agentic AI.
/// In a production system, this would call a real search API.
/// </summary>
/// <param name="query">The search query to look up.</param>
/// <param name="cancellationToken">Token to cancel the operation.</param>
/// <returns>Simulated search results as formatted text.</returns>
[Description("Searches the web for information on a given topic and returns relevant results.")]
static async Task<string> SearchAsync(
    [Description("The search query to look up")] string query,
    CancellationToken cancellationToken)
{
    await Task.Delay(100, cancellationToken);

    // Simulated search results for the demo topic
    var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["agentic AI software development"] = """
            1. "Agentic AI in Software Development" (TechReview, 2025)
               - AI agents now autonomously write, test, and deploy code
               - 73% of Fortune 500 companies use AI coding assistants
               - Key frameworks: Microsoft Agent Framework, LangChain, CrewAI

            2. "The Rise of Multi-Agent Systems" (DevOps Weekly, 2025)
               - Multi-agent architectures enable complex task decomposition
               - Agents collaborate through tool calling and message passing
               - Reduction of development cycle time by 40% in pilot programs

            3. "AI-Powered Code Review" (IEEE Software, 2025)
               - Automated code review agents catch 85% of common bugs
               - Security-focused agents identify vulnerabilities pre-deployment
               - Integration with CI/CD pipelines is now standard practice
            """,

        ["AI coding assistants 2025"] = """
            1. "State of AI Assistants Report" (Stack Overflow, 2025)
               - 89% of developers use AI assistants at least weekly
               - Top use cases: code generation, debugging, documentation
               - Average productivity increase of 35% reported

            2. "Enterprise AI Adoption" (Gartner, 2025)
               - Enterprise spending on AI dev tools reached $12B in 2025
               - .NET ecosystem sees rapid adoption of agent frameworks
               - Key trend: shift from copilots to autonomous agents
            """,

        ["multi-agent systems"] = """
            1. "Multi-Agent Collaboration Patterns" (Microsoft Research, 2025)
               - Sequential, parallel, and orchestrator patterns are dominant
               - Human-in-the-loop workflows for quality assurance
               - Agent specialization improves output quality by 60%

            2. "Building Reliable Agent Systems" (ArXiv, 2025)
               - Structured outputs reduce agent errors significantly
               - Tool-calling enables agents to interact with external systems
               - Workflow engines provide deterministic agent coordination
            """
    };

    // Find the best matching result
    string? bestMatch = results.Keys
        .FirstOrDefault(key => query.Contains(key, StringComparison.OrdinalIgnoreCase)
                            || key.Split(' ').Count(word =>
                                query.Contains(word, StringComparison.OrdinalIgnoreCase)) >= 2);

    if (bestMatch is not null)
    {
        return results[bestMatch];
    }

    // Return a generic result if no match
    return """
        1. "General AI Trends" (Tech Monthly, 2025)
           - AI continues to transform all aspects of software development
           - Agent-based systems are the fastest growing AI category
           - .NET and Python remain top platforms for AI development
        """;
}
