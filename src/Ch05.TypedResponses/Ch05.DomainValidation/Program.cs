// ============================================================================
// Domain Validation: shape, then meaning
// An employee's free-text expense claim becomes a typed record. The schema
// guarantees the shape; the domain rules decide whether it means anything
// possible. Every result is an explicit state, never a default:
//   Valid | Invalid(reasons) | Missing(fields) | Refused | Truncated | Unreadable
// The claim is only data: nothing here pays, books or sends anything.
//
//   dotnet run                    the minimal typed call, then the validated
//                                 pipeline, on three claims (needs a model)
//   dotnet run -- --self-test     offline proof, fake chat client, RESULT n/n
// ============================================================================

using System.Globalization;
using System.Text.Json;
using AgenticAI.Common.Extensions;
using Ch05.DomainValidation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();
ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DomainValidation");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Domain Validation — shape, then meaning ===\n");

ChatClientAgent agent = new(chatClient, instructions: ClaimPipeline.Instructions, name: "ClaimExtractor");
ClaimRules rules = new(AccountCurrency: "EUR");

string[] claims =
[
    "Taxi 64,80 EUR, 12 November, airport to the Fabrikam office",
    "Hotel in Lyon, two nights: 90.00 + 90.00 EUR, total 190.00 EUR",
    "Parking at the station for the Fabrikam workshop, receipt lost"
];

try
{
    foreach (string text in claims)
    {
        Console.WriteLine($"Claim: {text}");

        // 1. The minimal typed call: shape only.
        try
        {
            AgentSession session = await agent.CreateSessionAsync(cts.Token);
            AgentResponse<ExpenseClaim> response = await agent.RunAsync<ExpenseClaim>(
                ClaimPipeline.Prompt(text), session, ClaimJson.Options, cancellationToken: cts.Token);
            ExpenseClaim c = response.Result;
            Console.WriteLine(Inv($"  Typed call : {c.Amount} {c.Currency}, {c.Description}, {c.Date?.ToString("yyyy-MM-dd") ?? "no date"}"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Console.WriteLine($"  Typed call : {ex.GetType().Name}: {ex.Message}");
        }

        // 2. The validated pipeline: shape, then meaning.
        ClaimOutcome outcome = await ClaimPipeline.ExtractAsync(agent, text, rules, logger, cts.Token);
        Console.WriteLine($"  Validated  : {Describe(outcome)}");
        Console.WriteLine();
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("\n[Cancelled by user]");
}

Console.WriteLine("=== Domain Validation demo complete ===");
return 0;

internal partial class Program
{
    /// <summary>One line per outcome, in the invariant culture.</summary>
    internal static string Describe(ClaimOutcome outcome) => outcome switch
    {
        Valid v => Inv($"Valid: {v.Claim.Amount} {v.Claim.Currency}, {v.Claim.Description} (self-reported confidence {Confidence(v.Claim)}, not used)"),
        Invalid i => Inv($"Invalid: {string.Join("; ", i.Reasons)} (self-reported confidence {Confidence(i.Claim)}, not used)"),
        Missing m => $"Missing: {string.Join(", ", m.Fields)}",
        Refused r => $"Refused ({r.Raw?.Length ?? 0} chars kept)",
        Truncated t => $"Truncated ({t.Raw?.Length ?? 0} chars kept)",
        Unreadable u => $"Unreadable ({u.Raw?.Length ?? 0} chars kept): {u.Error}",
        _ => outcome.ToString()
    };

    private static string Confidence(ClaimDraft c) =>
        c.SelfReportedConfidence?.ToString(CultureInfo.InvariantCulture) ?? "absent";

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
