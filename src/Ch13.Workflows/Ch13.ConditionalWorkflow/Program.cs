// ============================================================================
// Chapter 12 — Introduction to Workflows (Conditional Workflow)
// Demonstrates conditional routing in the Workflow engine.
//
// Key concepts:
//   - Class-based executors: derive from Executor<TInput, TOutput>.
//   - AddEdge<T> with a predicate: typed condition controls edge activation.
//   - AddSwitch: multi-branch routing from a single node.
//   - Sentiment-based routing: positive → ThankYou, negative → Escalation.
//   - Multiple output nodes via WithOutputFrom(...).
//
// The workflow:
//   Input → SentimentAnalyzer → [ThankYouHandler | EscalationHandler | NeutralHandler]
// ============================================================================

using Microsoft.Agents.AI.Workflows;

Console.WriteLine("=== Chapter 12: Conditional Workflow — Sentiment Router ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string[] testMessages =
[
    "I love your product, it's amazing!",
    "This is terrible, everything is broken!",
    "Can you tell me your business hours?",
    "Thank you for the excellent support!"
];

// ---------------------------------------------------------------
// Approach A — Conditional edges with AddEdge<T>
// ---------------------------------------------------------------
Console.WriteLine("--- Approach A: Conditional Edges (AddEdge<T>) ---\n");

foreach (string message in testMessages)
{
    Console.WriteLine($"Input: \"{message}\"");

    Workflow conditionalWf = BuildConditionalEdgeWorkflow(message.GetHashCode().ToString("X"));
    Run run = await InProcessExecution.RunAsync(
        conditionalWf, message, sessionId: Guid.NewGuid().ToString(), cts.Token);

    foreach (WorkflowEvent evt in run.OutgoingEvents)
    {
        if (evt is WorkflowOutputEvent output)
        {
            Console.WriteLine($"  Result: {output.Data}");
        }
    }
    Console.WriteLine();
}

// ---------------------------------------------------------------
// Approach B — Switch-based routing
// ---------------------------------------------------------------
Console.WriteLine("--- Approach B: Switch Routing (AddSwitch) ---\n");

foreach (string message in testMessages)
{
    Console.WriteLine($"Input: \"{message}\"");

    Workflow switchWf = BuildSwitchWorkflow(message.GetHashCode().ToString("X"));
    Run run = await InProcessExecution.RunAsync(
        switchWf, message, sessionId: Guid.NewGuid().ToString(), cts.Token);

    foreach (WorkflowEvent evt in run.OutgoingEvents)
    {
        if (evt is WorkflowOutputEvent output)
        {
            Console.WriteLine($"  Result: {output.Data}");
        }
    }
    Console.WriteLine();
}

Console.WriteLine("=== Conditional Workflow demo complete! ===");

// ---------------------------------------------------------------
// Workflow builders (fresh instances for each run)
// ---------------------------------------------------------------

/// <summary>
/// Builds a workflow using typed conditional edges (AddEdge with predicate).
/// Each edge has a condition that checks the sentiment of the analysis result.
/// </summary>
Workflow BuildConditionalEdgeWorkflow(string suffix)
{
    ExecutorBinding analyzer = new SentimentAnalyzer($"analyzer-ce-{suffix}").BindExecutor();
    ExecutorBinding thankYou = new ThankYouHandler($"thankyou-ce-{suffix}").BindExecutor();
    ExecutorBinding escalation = new EscalationHandler($"escalation-ce-{suffix}").BindExecutor();
    ExecutorBinding neutral = new NeutralHandler($"neutral-ce-{suffix}").BindExecutor();

    return new WorkflowBuilder(analyzer)
        .AddEdge<SentimentResult>(analyzer, thankYou,
            r => r is not null && r.Sentiment == Sentiment.Positive)
        .AddEdge<SentimentResult>(analyzer, escalation,
            r => r is not null && r.Sentiment == Sentiment.Negative)
        .AddEdge<SentimentResult>(analyzer, neutral,
            r => r is not null && r.Sentiment == Sentiment.Neutral)
        .WithOutputFrom(thankYou, escalation, neutral)
        .Build();
}

/// <summary>
/// Builds a workflow using AddSwitch for multi-branch routing.
/// The switch evaluates conditions in order and routes to the matching case.
/// </summary>
Workflow BuildSwitchWorkflow(string suffix)
{
    ExecutorBinding analyzer = new SentimentAnalyzer($"analyzer-sw-{suffix}").BindExecutor();
    ExecutorBinding thankYou = new ThankYouHandler($"thankyou-sw-{suffix}").BindExecutor();
    ExecutorBinding escalation = new EscalationHandler($"escalation-sw-{suffix}").BindExecutor();
    ExecutorBinding neutral = new NeutralHandler($"neutral-sw-{suffix}").BindExecutor();

    return new WorkflowBuilder(analyzer)
        .AddSwitch(analyzer, sw => sw
            .AddCase<SentimentResult>(
                r => r is not null && r.Sentiment == Sentiment.Positive,
                [thankYou])
            .AddCase<SentimentResult>(
                r => r is not null && r.Sentiment == Sentiment.Negative,
                [escalation])
            .WithDefault([neutral]))
        .WithOutputFrom(thankYou, escalation, neutral)
        .Build();
}

// ---------------------------------------------------------------
// Executors
// ---------------------------------------------------------------

/// <summary>Analyzes sentiment and tags the message.</summary>
internal sealed class SentimentAnalyzer(string id) : Executor<string, SentimentResult>(id)
{
    public override ValueTask<SentimentResult> HandleAsync(
        string input, IWorkflowContext context, CancellationToken ct)
    {
        string lower = input.ToLowerInvariant();

        Sentiment sentiment;
        if (lower.Contains("great") || lower.Contains("love") ||
            lower.Contains("excellent") || lower.Contains("happy") ||
            lower.Contains("thank") || lower.Contains("amazing"))
        {
            sentiment = Sentiment.Positive;
        }
        else if (lower.Contains("terrible") || lower.Contains("hate") ||
                 lower.Contains("awful") || lower.Contains("angry") ||
                 lower.Contains("broken") || lower.Contains("worst"))
        {
            sentiment = Sentiment.Negative;
        }
        else
        {
            sentiment = Sentiment.Neutral;
        }

        Console.WriteLine($"  [Analyzer] \"{input}\" → {sentiment}");
        return ValueTask.FromResult(new SentimentResult(input, sentiment));
    }
}

/// <summary>Handles positive sentiment with a thank-you response.</summary>
internal sealed class ThankYouHandler(string id) : Executor<SentimentResult, string>(id)
{
    public override ValueTask<string> HandleAsync(
        SentimentResult result, IWorkflowContext context, CancellationToken ct)
    {
        string response = $"Thank you for your positive feedback! " +
                          $"We're glad to hear: \"{result.Message}\"";
        Console.WriteLine($"  [ThankYou] {response}");
        return ValueTask.FromResult(response);
    }
}

/// <summary>Handles negative sentiment by escalating to support.</summary>
internal sealed class EscalationHandler(string id) : Executor<SentimentResult, string>(id)
{
    public override ValueTask<string> HandleAsync(
        SentimentResult result, IWorkflowContext context, CancellationToken ct)
    {
        string response = $"ESCALATION: Customer issue detected. " +
                          $"Original message: \"{result.Message}\" — " +
                          $"Routing to senior support team.";
        Console.WriteLine($"  [Escalation] {response}");
        return ValueTask.FromResult(response);
    }
}

/// <summary>Handles neutral sentiment with a standard acknowledgment.</summary>
internal sealed class NeutralHandler(string id) : Executor<SentimentResult, string>(id)
{
    public override ValueTask<string> HandleAsync(
        SentimentResult result, IWorkflowContext context, CancellationToken ct)
    {
        string response = $"Thank you for your message. " +
                          $"We've noted: \"{result.Message}\"";
        Console.WriteLine($"  [Neutral] {response}");
        return ValueTask.FromResult(response);
    }
}

// ---------------------------------------------------------------
// Types
// ---------------------------------------------------------------

/// <summary>Represents the detected sentiment of a message.</summary>
internal enum Sentiment
{
    /// <summary>Positive sentiment detected.</summary>
    Positive,

    /// <summary>Negative sentiment detected.</summary>
    Negative,

    /// <summary>Neutral or ambiguous sentiment.</summary>
    Neutral
}

/// <summary>
/// Carries the original message together with its detected sentiment
/// through the workflow pipeline.
/// </summary>
/// <param name="Message">The original user message.</param>
/// <param name="Sentiment">The detected sentiment.</param>
internal sealed record SentimentResult(string Message, Sentiment Sentiment);
