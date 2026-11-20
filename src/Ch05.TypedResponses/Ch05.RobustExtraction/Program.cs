// ============================================================================
// Structured Output (Robust Extraction)
// The production counterpart to the happy path. Three ideas, together:
//   1. [Description] attributes carry schema hints independent of build config.
//   2. TryRunStructuredAsync<T>() wraps RunAsync<T>() with a single stricter
//      retry, returning a Success<T>/Failure<T> result that keeps the raw reply.
//   3. Soft validation (confidence + cross-field invariants) flags suspect
//      extractions for review — it never silently "fixes" the model's output.
//
// The "confidence" field is SELF-REPORTED by the model, not calibrated. It is
// range-checked (0.0..1.0; missing, NaN or string values are flagged or
// rejected) and never gates on its own: an extraction is auto-processed only
// when every soft-validation rule passes.
// Offline check (canned JSON and a fake chat client, no LLM): dotnet run -- --self-test
// ============================================================================

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticAI.Common.Extensions;
using Ch05.RobustExtraction;

if (args.Contains("--self-test")) return await InvoiceValidation.SelfTestAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RobustExtraction");

Console.WriteLine("=== Structured Output — Robust Extraction ===\n");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Shared with the self-test, so the offline checks parse exactly as the run does.
JsonSerializerOptions jsonOptions = InvoiceValidation.JsonOptions;

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are an invoice data extraction engine.
        Extract structured data from billing notes and invoices.
        Respond with valid JSON matching the InvoiceExtraction schema and nothing else.
        Set confidence to a JSON number (not a string) from 0.0 to 1.0 for how sure you are.
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
                Console.WriteLine($"  Confidence : {ConfidenceCheck.Describe(inv.SelfReportedConfidence)}");
                if (inv.LineItems.Count > 0)
                {
                    Console.WriteLine("  Line Items :");
                    foreach (string item in inv.LineItems)
                    {
                        Console.WriteLine($"    - {item}");
                    }
                }

                // Soft validation — flag, never auto-correct. A high self-reported
                // confidence never clears a warning; only a clean pass on every
                // rule routes the extraction automatically.
                List<string> warnings = [.. InvoiceValidation.Validate(inv)];
                Console.WriteLine($"  Route      : {(warnings.Count == 0 ? "auto-process (no soft-validation warnings)" : "manual review")}");
                foreach (string warning in warnings)
                {
                    Console.WriteLine($"    ! {warning}");
                }
                break;

            case Failure<InvoiceExtraction> fail:
                Console.WriteLine($"  Extraction FAILED (finish reason: {fail.FinishReason?.Value ?? "none"}):");
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
return 0;
