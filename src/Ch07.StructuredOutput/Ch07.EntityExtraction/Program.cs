// ============================================================================
// Chapter 7 — Structured Output (Entity Extraction)
// Extract structured data from free-text customer feedback.
// Demonstrates real-world entity extraction with enums, validation,
// and edge-case handling.
//
// The agent reads raw customer feedback and produces a FeedbackAnalysis
// record with sentiment, urgency, categories, issues, and suggested actions.
// ============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch07.EntityExtraction;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Chapter 7: Entity Extraction from Customer Feedback ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

JsonSerializerOptions jsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// Create the extraction agent with detailed instructions
ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a customer feedback analysis engine.
        Extract structured data from customer feedback text.
        Always respond with valid JSON matching the FeedbackAnalysis schema.

        Rules:
        - sentiment: must be "Positive", "Neutral", or "Negative"
        - urgency: must be "Low", "Medium", "High", or "Critical"
        - If the customer name is not mentioned, use "Unknown"
        - confidence: a float between 0.0 and 1.0 indicating extraction confidence
        - Always provide at least one suggested action
        - Be specific in issue extraction — quote relevant phrases when possible
        - Do not include any text outside the JSON object
        """,
    name: "FeedbackAnalyzer");

// ---------------------------------------------------------------
// Sample customer feedback texts (varying sentiment and complexity)
// ---------------------------------------------------------------
Dictionary<string, string> feedbackSamples = new()
{
    ["Clear Negative"] = """
        Hi, my name is Sarah Chen. I purchased your CloudSync Pro subscription
        last month and I'm extremely frustrated. The sync feature fails every
        time I try to upload files larger than 50MB, and customer support has
        been completely unhelpful — I've been waiting 5 days for a response.
        I'm considering cancelling my subscription and switching to a competitor.
        """,

    ["Enthusiastic Positive"] = """
        Just wanted to say that your new IDE plugin "CodeLens AI" is absolutely
        fantastic! The code suggestions are incredibly accurate and it's saved
        me at least 2 hours per day. The integration with Visual Studio was
        seamless. Would love to see support for JetBrains Rider too!
        """,

    ["Mixed Feedback"] = """
        I've been using the DataPipeline tool for our ETL workflows. The
        performance is great — it handles our 10GB daily load without issues.
        However, the documentation is severely lacking. We spent three days
        trying to figure out the retry configuration because the docs are
        outdated. Also, the pricing just increased by 40% with no notice.
        Please improve communication about changes.
        """,

    ["Vague / Edge Case"] = """
        It's okay I guess. Works sometimes. The thing with the buttons
        could be better. Not sure if I'll keep using it.
        """
};

try
{
    foreach ((string label, string feedback) in feedbackSamples)
    {
        Console.WriteLine($"--- {label} ---");
        Console.WriteLine($"Feedback: {feedback.Trim()}\n");

        AgentSession session = await agent.CreateSessionAsync(cts.Token);

        AgentResponse<FeedbackAnalysis> response = await agent.RunAsync<FeedbackAnalysis>(
            $"Analyze this customer feedback:\n\n{feedback}",
            session,
            jsonOptions,
            cancellationToken: cts.Token);

        FeedbackAnalysis analysis = response.Result;

        // ---------------------------------------------------------------
        // Display the extracted entities
        // ---------------------------------------------------------------
        Console.WriteLine($"  Customer   : {analysis.CustomerName}");
        Console.WriteLine($"  Sentiment  : {analysis.Sentiment}");
        Console.WriteLine($"  Urgency    : {analysis.Urgency}");
        Console.WriteLine($"  Category   : {analysis.Category}");
        Console.WriteLine($"  Product    : {analysis.Product}");
        Console.WriteLine($"  Confidence : {analysis.Confidence:P0}");
        Console.WriteLine($"  Summary    : {analysis.Summary}");

        if (analysis.Issues.Count > 0)
        {
            Console.WriteLine($"  Issues     :");
            foreach (string issue in analysis.Issues)
            {
                Console.WriteLine($"    - {issue}");
            }
        }

        if (analysis.PositiveAspects.Count > 0)
        {
            Console.WriteLine($"  Positives  :");
            foreach (string positive in analysis.PositiveAspects)
            {
                Console.WriteLine($"    + {positive}");
            }
        }

        Console.WriteLine($"  Actions    :");
        foreach (string action in analysis.SuggestedActions)
        {
            Console.WriteLine($"    * {action}");
        }

        // ---------------------------------------------------------------
        // Validation: check for edge cases
        // ---------------------------------------------------------------
        List<string> warnings = [];

        if (analysis.Confidence < 0.5)
        {
            warnings.Add($"Low confidence ({analysis.Confidence:P0}) — manual review recommended");
        }

        if (analysis.Urgency == Urgency.Critical && analysis.Sentiment != Sentiment.Negative)
        {
            warnings.Add("Critical urgency with non-negative sentiment — verify classification");
        }

        if (analysis.Issues.Count == 0 && analysis.Sentiment == Sentiment.Negative)
        {
            warnings.Add("Negative sentiment but no issues extracted — extraction may be incomplete");
        }

        if (string.IsNullOrWhiteSpace(analysis.Product))
        {
            warnings.Add("No product identified — may need manual categorization");
        }

        if (warnings.Count > 0)
        {
            Console.WriteLine($"  Warnings   :");
            foreach (string warning in warnings)
            {
                Console.WriteLine($"    ! {warning}");
            }
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
    Console.WriteLine($"\nJSON deserialization error: {ex.Message}");
    Console.WriteLine("The LLM response did not match the expected FeedbackAnalysis schema.");
}

Console.WriteLine("=== Entity Extraction demo complete! ===");
