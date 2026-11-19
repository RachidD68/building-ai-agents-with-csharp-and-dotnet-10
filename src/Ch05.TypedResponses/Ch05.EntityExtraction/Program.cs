// ============================================================================
// Structured Output (Entity Extraction)
// Extract structured data from free-text customer feedback.
// Demonstrates real-world entity extraction with enums, validation,
// and edge-case handling.
//
// The agent reads raw customer feedback and produces a FeedbackAnalysis
// record with sentiment, urgency, categories, issues, and suggested actions.
//
// The "confidence" field is SELF-REPORTED by the model, not calibrated. It is
// range-checked (0.0..1.0; missing, NaN or string values are flagged or
// rejected) and never gates on its own: an extraction is auto-processed only
// when every soft-validation rule passes.
// Offline check of that logic (JSON parsing only, no LLM): dotnet run -- --self-test
// ============================================================================

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;
using Ch05.EntityExtraction;

if (args.Contains("--self-test")) return FeedbackValidation.SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

Console.WriteLine("=== Entity Extraction from Customer Feedback ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Shared with the self-test, so the offline checks parse exactly as the run does.
JsonSerializerOptions jsonOptions = FeedbackValidation.JsonOptions;

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
        - confidence: a JSON number (not a string) from 0.0 to 1.0 for how sure you are
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
        Console.WriteLine($"  Confidence : {ConfidenceCheck.Describe(analysis.SelfReportedConfidence)}");
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
        // Validation: confidence range check + cross-field invariants.
        // A high self-reported confidence never clears a warning; only a
        // clean pass on every rule routes the extraction automatically.
        // ---------------------------------------------------------------
        List<string> warnings = FeedbackValidation.Validate(analysis);
        Console.WriteLine($"  Route      : {(warnings.Count == 0 ? "auto-process (no soft-validation warnings)" : "manual review")}");

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
return 0;
