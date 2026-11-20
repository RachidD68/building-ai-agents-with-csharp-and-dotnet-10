// ============================================================================
// Exercise 03 — Invoice Validator (Challenge, solution)
// An invoice extracted by TryRunStructuredAsync<T> is checked for meaning:
//   - the vendor is known and the invoice is in that vendor's currency
//   - every amount and the exchange rate are positive
//   - the line amounts times the rate equal the total, within 0.01
// Each outcome is explicit: Accepted, Rejected(reasons), Missing(fields) or
// Failed(message). Nothing is paid; the invoice is only data.
//
//   dotnet run                    one invoice through the model
//   dotnet run -- --self-test     offline suite, fake chat client, RESULT n/n
// ============================================================================

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticAI.Common.Extensions;
using Ch05.RobustExtraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--self-test")) return await Suite.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InvoiceValidator");

const string Text = """
    Invoice from Northwind Cloud Services, billed in USD.
    Compute: 300.00 USD. Storage: 120.00 USD.
    Converted at 0.92, the total charged to the EUR account is 386.40 EUR.
    """;

Console.WriteLine("=== Invoice Validator ===\n");
Console.WriteLine(Text);
ChatClientAgent agent = new(chatClient, instructions: InvoiceCheck.Instructions, name: "InvoiceExtractor");
InvoiceOutcome outcome = await InvoiceCheck.ExtractAsync(agent, Text, logger);
Console.WriteLine($"Outcome: {outcome.Describe()}");
return 0;

/// <summary>What the model is asked to return. Every amount may be absent.</summary>
public sealed record InvoiceDraft
{
    [Description("The vendor that issued the invoice")] public string? Vendor { get; init; }
    [Description("ISO 4217 code of the invoice's own currency, in capitals")] public string? Currency { get; init; }
    [Description("One entry per billed item, amounts in the invoice's currency")] public IReadOnlyList<InvoiceLine> Lines { get; init; } = [];
    [Description("Exchange rate from the invoice's currency to EUR; 1 when the invoice is in EUR")]
    [JsonNumberHandling(JsonNumberHandling.Strict)] public decimal? Rate { get; init; }
    [Description("Total charged to the EUR account, as a JSON number")]
    [JsonNumberHandling(JsonNumberHandling.Strict)] public decimal? TotalEur { get; init; }
}

public sealed record InvoiceLine(
    [property: Description("What was billed")] string? Description,
    [property: Description("Amount in the invoice's currency")]
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] decimal? Amount);

public abstract record InvoiceOutcome
{
    public string Describe() => this switch
    {
        Accepted a => $"Accepted: {a.Invoice.Vendor}, {a.Invoice.TotalEur?.ToString(CultureInfo.InvariantCulture)} EUR",
        Rejected r => $"Rejected: {string.Join("; ", r.Reasons)}",
        MissingFields m => $"Missing: {string.Join(", ", m.Fields)}",
        Failed f => $"Failed: {f.Message}",
        _ => ToString()
    };
}
public sealed record Accepted(InvoiceDraft Invoice) : InvoiceOutcome;
public sealed record Rejected(IReadOnlyList<string> Reasons) : InvoiceOutcome;
public sealed record MissingFields(IReadOnlyList<string> Fields) : InvoiceOutcome;
public sealed record Failed(string Message) : InvoiceOutcome;

public static class InvoiceCheck
{
    public const string Instructions = """
        You extract invoices into JSON. Amounts are JSON numbers with a dot as the
        decimal separator. Currencies are ISO 4217 codes in capitals. Use null for
        any field the invoice does not state; never compute a missing value.
        """;

    /// <summary>The currency each known vendor bills in.</summary>
    public static readonly IReadOnlyDictionary<string, string> VendorCurrency = new Dictionary<string, string>
    {
        ["Northwind Cloud Services"] = "USD",
        ["Fabrikam"] = "EUR",
    };

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerOptions.Web);

    public static async Task<InvoiceOutcome> ExtractAsync(AIAgent agent, string text, ILogger logger)
    {
        Result<InvoiceDraft> result = await agent.TryRunStructuredAsync<InvoiceDraft>(
            $"Extract the invoice from this text:\n\n{text}", JsonOptions, logger);
        return result switch
        {
            Success<InvoiceDraft> ok => Validate(ok.Value),
            Failure<InvoiceDraft> f => new Failed($"{f.FinishReason?.Value ?? "unreadable"}: {string.Join("; ", f.Messages)}"),
            _ => throw new InvalidOperationException("unknown result")
        };
    }

    public static InvoiceOutcome Validate(InvoiceDraft inv)
    {
        IReadOnlyList<InvoiceLine?> lines = inv.Lines ?? [];
        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(inv.Vendor)) missing.Add("vendor");
        if (string.IsNullOrWhiteSpace(inv.Currency)) missing.Add("currency");
        if (inv.Rate is null) missing.Add("rate");
        if (inv.TotalEur is null) missing.Add("totalEur");
        if (lines.Count == 0 || lines.Any(l => l?.Amount is null)) missing.Add("line amounts");
        if (missing.Count > 0) return new MissingFields(missing);

        List<string> reasons = [];
        if (!VendorCurrency.TryGetValue(inv.Vendor!, out string? expected))
            reasons.Add($"unknown_vendor: '{inv.Vendor}'");
        else if (inv.Currency != expected)
            reasons.Add($"currency_not_vendor: {inv.Currency}, {inv.Vendor} bills in {expected}");

        decimal rate = inv.Rate!.Value, total = inv.TotalEur!.Value;
        if (rate <= 0m) reasons.Add(Inv($"rate_not_positive: {rate}"));
        if (total <= 0m) reasons.Add(Inv($"total_not_positive: {total}"));
        if (lines.Any(l => l!.Amount <= 0m)) reasons.Add("line_not_positive");

        decimal expectedTotal = lines.Sum(l => l!.Amount!.Value) * rate;
        if (Math.Abs(expectedTotal - total) > 0.01m)
            reasons.Add(Inv($"total_mismatch: lines x rate = {expectedTotal}, total is {total}"));

        return reasons.Count == 0 ? new Accepted(inv) : new Rejected(reasons);
    }

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);
}

internal static class Suite
{
    private const string Good = """
        {"vendor":"Northwind Cloud Services","currency":"USD",
         "lines":[{"description":"Compute","amount":300.00},{"description":"Storage","amount":120.00}],
         "rate":0.92,"totalEur":386.40}
        """;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Invoice Validator — offline suite ===\n");
        int passed = 0, total = 0;

        async Task Case(string name, Func<InvoiceOutcome, bool> expected, params (string, ChatFinishReason)[] replies)
        {
            CannedChatClient client = new(replies);
            ChatClientAgent agent = new(client, instructions: InvoiceCheck.Instructions);
            InvoiceOutcome outcome = await InvoiceCheck.ExtractAsync(agent, "invoice text", NullLogger.Instance);
            bool ok = expected(outcome);
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}  [{outcome.Describe()}]");
        }

        static bool Reason(InvoiceOutcome o, string code) => o is Rejected r && r.Reasons.Any(x => x.StartsWith(code, StringComparison.Ordinal));
        static (string, ChatFinishReason) Stop(string json) => CannedChatClient.Stop(json);

        await Case("a consistent USD invoice -> Accepted", o => o is Accepted, Stop(Good));
        await Case("schema-valid, billed in EUR by a USD vendor -> Rejected currency_not_vendor",
            o => Reason(o, "currency_not_vendor"), Stop(Good.Replace("\"USD\"", "\"EUR\"")));
        await Case("schema-valid, total 420.00 without the rate -> Rejected total_mismatch",
            o => Reason(o, "total_mismatch"), Stop(Good.Replace("386.40", "420.00")));
        await Case("a 0.004 rounding gap is within 0.01 -> Accepted",
            o => o is Accepted, Stop(Good.Replace("386.40", "386.404")));
        await Case("a negative line -> Rejected line_not_positive",
            o => Reason(o, "line_not_positive"), Stop(Good.Replace("120.00", "-120.00")));
        await Case("rate 0 -> Rejected rate_not_positive", o => Reason(o, "rate_not_positive"), Stop(Good.Replace("0.92", "0")));
        await Case("an unknown vendor -> Rejected unknown_vendor",
            o => Reason(o, "unknown_vendor"), Stop(Good.Replace("Northwind Cloud Services", "Contoso Supplies")));
        await Case("total absent -> Missing totalEur, never 0",
            o => o is MissingFields { Fields: ["totalEur"] }, Stop(Good.Replace(",\"totalEur\":386.40", "")));
        await Case("prose, then valid JSON -> Accepted after one retry",
            o => o is Accepted, Stop("Here is your invoice."), Stop(Good));
        await Case("cut off at the output limit -> Failed length, no default invoice",
            o => o is Failed f && f.Message.StartsWith("length"), ("""{"vendor":"North""", ChatFinishReason.Length));

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
