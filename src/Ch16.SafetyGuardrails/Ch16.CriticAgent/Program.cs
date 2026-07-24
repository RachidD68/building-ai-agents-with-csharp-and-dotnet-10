// ============================================================================
// Chapter 16 — Safety and Guardrails (Critic Agent)
// Demonstrates using a second "critic" agent to validate the output of a
// primary agent. The critic evaluates responses for:
//   - Factual consistency
//   - Appropriate tone
//   - PII leakage
//
// Uses RunAsync<T>() to get a structured ValidationResult from the critic,
// enabling programmatic decision-making based on the evaluation.
//
// Pattern: Primary Agent → Critic Agent → Validated Output
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Ch16.CriticAgent.Models;
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

Console.WriteLine("=== Chapter 16: Safety — Critic Agent ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

JsonSerializerOptions jsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// ---------------------------------------------------------------
// Step 1: Create the primary (content-generating) agent
// ---------------------------------------------------------------
ChatClientAgent primaryAgent = new(
    chatClient,
    instructions: """
        You are a technical documentation writer. Write clear, accurate
        explanations of .NET concepts. Use examples when helpful.
        Keep responses under 150 words.
        """,
    name: "ContentWriter");

// ---------------------------------------------------------------
// Step 2: Create the critic (validation) agent
// ---------------------------------------------------------------
ChatClientAgent criticAgent = new(
    chatClient,
    instructions: """
        You are a strict quality assurance reviewer for technical content.
        You evaluate responses and return a JSON object with your assessment.

        Evaluate these dimensions:
        1. Factual accuracy — Is the content technically correct?
        2. Tone — Is it professional and appropriate for documentation?
        3. PII — Does it contain any personal information (emails, names, etc.)?

        Score from 1 (poor) to 10 (excellent). Pass threshold is 7 or above.
        Always respond with valid JSON matching the requested schema.
        """,
    name: "QualityCritic");

// ---------------------------------------------------------------
// Step 3: Run the primary-critic pipeline
// ---------------------------------------------------------------
string[] prompts =
[
    "Explain the difference between value types and reference types in C#.",
    "What is the IDisposable pattern and when should you use it?"
];

try
{
    foreach (string prompt in prompts)
    {
        Console.WriteLine($"--- Prompt: \"{prompt}\" ---\n");

        // Primary agent generates content
        AgentSession primarySession = await primaryAgent.CreateSessionAsync(cts.Token);
        AgentResponse primaryResponse = await primaryAgent.RunAsync(
            prompt,
            primarySession,
            cancellationToken: cts.Token);

        string generatedContent = primaryResponse.Text ?? string.Empty;
        Console.WriteLine($"[{primaryAgent.Name}] Generated:\n{generatedContent}\n");

        // Critic agent evaluates the content
        string criticPrompt = $"""
            Evaluate the following technical response for quality.

            Original question: {prompt}

            Response to evaluate:
            {generatedContent}
            """;

        AgentSession criticSession = await criticAgent.CreateSessionAsync(cts.Token);
        AgentResponse<ValidationResult> criticResponse =
            await criticAgent.RunAsync<ValidationResult>(
                criticPrompt,
                criticSession,
                jsonOptions,
                cancellationToken: cts.Token);

        ValidationResult validation = criticResponse.Result;

        // Display the validation result
        Console.WriteLine($"[{criticAgent.Name}] Evaluation:");
        Console.WriteLine($"  Passed    : {(validation.Passed ? "YES" : "NO")}");
        Console.WriteLine($"  Score     : {validation.Score}/10");
        Console.WriteLine($"  Factual   : {validation.FactualAssessment}");
        Console.WriteLine($"  Tone      : {validation.ToneAssessment}");
        Console.WriteLine($"  PII Found : {(validation.PiiDetected ? "YES" : "No")}");

        if (validation.Issues.Count > 0)
        {
            Console.WriteLine("  Issues    :");
            foreach (string issue in validation.Issues)
            {
                Console.WriteLine($"    - {issue}");
            }
        }

        if (validation.Suggestions.Count > 0)
        {
            Console.WriteLine("  Suggestions:");
            foreach (string suggestion in validation.Suggestions)
            {
                Console.WriteLine($"    - {suggestion}");
            }
        }

        // Decision gate: only surface content that passes validation
        Console.WriteLine();
        if (validation.Passed)
        {
            Console.WriteLine("  >>> APPROVED — Content passes quality gate.");
        }
        else
        {
            Console.WriteLine("  >>> REJECTED — Content failed quality gate. " +
                              "Would need revision before publishing.");
        }

        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}
catch (JsonException ex)
{
    Console.WriteLine($"\nCritic returned invalid JSON: {ex.Message}");
    Console.WriteLine("This can happen with less capable models.");
}

Console.WriteLine("=== Critic Agent demo complete! ===");
