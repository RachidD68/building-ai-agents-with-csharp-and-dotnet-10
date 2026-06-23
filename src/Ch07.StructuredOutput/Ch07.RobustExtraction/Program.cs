// ============================================================================
// Chapter 7 — Structured Output (Robust Extraction)
// The production counterpart to the happy path. Three ideas, together:
//   1. [Description] attributes carry schema hints independent of build config.
//   2. TryRunStructuredAsync<T>() wraps RunAsync<T>() with a single stricter
//      retry and an untyped fallback, returning a Success<T>/Failure<T> result.
//   3. Soft validation (confidence + cross-field invariants) flags suspect
//      extractions for review — it never silently "fixes" the model's output.
// ============================================================================

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticAI.Common.Extensions;
using Ch07.RobustExtraction;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RobustExtraction");

Console.WriteLine("=== Chapter 7: Structured Output — Robust Extraction ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

JsonSerializerOptions jsonOptions = new(JsonSerializerOptions.Web)
{
    WriteIndented = true
};

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are an invoice data extraction engine.
        Extract structured data from billing notes and invoices.
        Respond with valid JSON matching the InvoiceExtraction schema and nothing else.
        Set confidence between 0.0 and 1.0 to reflect how sure you are.
        """,
    name: "InvoiceExtractor");

Dictionary<string, string> invoices = new()
{
    ["Clean invoice"] = """
        INVOICE #INV-2026-0412
        From: Northwind Cloud Services
        For: Compute (320 hrs), Object storage (2 TB), Egress (140 GB)
        Total due: 4,820.00 USD — net 30, no discount.
        """,

    ["Overdue note"] = """
        Reminder: invoice AC-9981 from Acme Hardware for $1,275.50 is now 14 days
        past due. A 2% late fee applies. Please remit immediately to avoid suspension.
        """
};

try
{
    foreach ((string label, string text) in invoices)
    {
        Console.WriteLine($"--- {label} ---");
        Console.WriteLine(text.Trim());
        Console.WriteLine();

        Result<InvoiceExtraction> result = await agent.TryRunStructuredAsync<InvoiceExtraction>(
            $"Extract the invoice data from this note:\n\n{text}",
            jsonOptions,
            logger,
            cts.Token);

        switch (result)
        {
            case Success<InvoiceExtraction> ok:
                InvoiceExtraction inv = ok.Value;
                Console.WriteLine($"  Vendor     : {inv.Vendor}");
                Console.WriteLine($"  Invoice #  : {inv.InvoiceNumber}");
                Console.WriteLine($"  Amount Due : {inv.AmountDue:N2} {inv.Currency}");
                Console.WriteLine($"  Priority   : {inv.Priority}");
                Console.WriteLine($"  Confidence : {inv.Confidence:P0}");
                if (inv.LineItems.Count > 0)
                {
                    Console.WriteLine("  Line Items :");
                    foreach (string item in inv.LineItems)
                    {
                        Console.WriteLine($"    - {item}");
                    }
                }

                // Soft validation — flag, never auto-correct.
                foreach (string warning in Validate(inv))
                {
                    Console.WriteLine($"    ! {warning}");
                }
                break;

            case Failure<InvoiceExtraction> fail:
                Console.WriteLine("  Extraction FAILED after one retry:");
                foreach (string message in fail.Messages)
                {
                    Console.WriteLine($"    ! {message}");
                }
                Console.WriteLine($"  Raw response logged for review ({fail.RawResponse?.Length ?? 0} chars).");
                break;
        }

        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("=== Robust Extraction demo complete! ===");

// Cross-field and threshold checks. Returns human-readable warnings; the caller
// decides whether to route for review, log, or retry.
static IEnumerable<string> Validate(InvoiceExtraction inv)
{
    if (inv.Confidence < 0.5)
    {
        yield return $"Low confidence ({inv.Confidence:P0}) — manual review recommended.";
    }

    if (inv.AmountDue <= 0)
    {
        yield return "Amount due is zero or negative — verify extraction.";
    }

    if (inv.Priority == PaymentPriority.Urgent && inv.AmountDue == 0)
    {
        yield return "Urgent priority with no amount due — inconsistent.";
    }

    if (string.IsNullOrWhiteSpace(inv.Currency))
    {
        yield return "No currency identified — may need manual categorization.";
    }
}
