using System.Globalization;
using System.Text.Json;
using Ch05.RobustExtraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ch05.DomainValidation;

/// <summary>
/// Offline proof, no model and no key: canned replies through a fake chat
/// client, then the validator. dotnet run -- --self-test prints RESULT n/n and
/// exits with 1 on any failure.
/// </summary>
internal static class SelfTest
{
    private const string Taxi = "Taxi 64,80 EUR, 12 November, airport to the Fabrikam office";
    private static readonly ClaimRules Rules = new("EUR");
    private static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Domain Validation — self-test (offline) ===");

        Console.WriteLine("\n-- What RunAsync<T> does (fake chat client) --");
        CannedChatClient probe = new(CannedChatClient.Stop("""{"amount": 64.8"""));
        AgentResponse<ExpenseClaim> lazy = await RunMinimalAsync(probe);
        Expect("RunAsync<T> sends a JSON-schema response format named after the type",
            probe.LastResponseFormat is ChatResponseFormatJson { Schema: not null, SchemaName: "ExpenseClaim" });
        Expect("RunAsync<T> returns without parsing; FinishReason is readable on AgentResponse<T>",
            lazy.FinishReason == ChatFinishReason.Stop);
        Expect("broken JSON throws JsonException only when .Result is read", Throws<JsonException>(() => lazy.Result));
        AgentResponse<ExpenseClaim> empty = await RunMinimalAsync(new CannedChatClient(CannedChatClient.Stop("")));
        Expect("DEFECT REPRODUCED: an empty reply throws InvalidOperationException, which catch (JsonException) misses",
            Throws<InvalidOperationException>(() => empty.Result));

        Console.WriteLine("\n-- Defect reproductions (the minimal typed call accepts them) --");
        ExpenseClaim noAmount = await MinimalAsync("""{"description":"Taxi","currency":"EUR","date":"2026-11-12"}""");
        Expect("DEFECT REPRODUCED: a missing amount key becomes 0m, which passes a <= 50 limit",
            noAmount.Amount == 0m && noAmount.Amount <= 50m);
        ExpenseClaim usd = await MinimalAsync("""{"description":"Taxi","amount":64.80,"currency":"USD","date":"2026-11-12"}""");
        Expect("DEFECT REPRODUCED: a USD claim against a EUR account deserializes without complaint", usd.Currency == "USD");
        ExpenseClaim negative = await MinimalAsync("""{"description":"Taxi","amount":-64.80,"currency":"EUR","date":null}""");
        Expect("DEFECT REPRODUCED: a negative amount deserializes without complaint", negative.Amount < 0m);
        Expect("DEFECT REPRODUCED: decimal.Parse(\"64,80\") under en-US gives 6480",
            decimal.Parse("64,80", CultureInfo.GetCultureInfo("en-US")) == 6480m);
        Expect("the shape-only fix exists: RespectRequiredConstructorParameters turns the missing key into a JsonException",
            Throws<JsonException>(() => JsonSerializer.Deserialize<ExpenseClaim>("""{"description":"Taxi","currency":"EUR"}""",
                new JsonSerializerOptions(JsonSerializerOptions.Web) { RespectRequiredConstructorParameters = true })));

        Console.WriteLine("\n-- The ladder: shape, then meaning --");
        const string Sound = """{"description":"Taxi","amount":64.80,"currency":"EUR","date":"2026-11-12","lines":[],"confidence":0.9}""";
        Check("the Contoso taxi claim (64,80 in the text, 64.80 in JSON) -> Valid", Sound, Taxi, o => o is Valid);
        Check("the same claim under fr-FR and en-US current culture -> Valid both times", Sound, Taxi,
            o => o is Valid && UnderCulture("fr-FR", () => Validate(Sound, Taxi)) is Valid
                            && UnderCulture("en-US", () => Validate(Sound, Taxi)) is Valid);
        Check("amount absent -> Missing [amount], never 0",
            """{"description":"Taxi","currency":"EUR"}""", Taxi, o => o is Missing { Fields: ["amount"] });
        Check("amount null and currency null -> Missing [amount, currency]",
            """{"description":"Taxi","amount":null,"currency":null}""", Taxi, o => o is Missing { Fields: ["amount", "currency"] });
        Check("USD on a EUR account -> Invalid currency_mismatch",
            Sound.Replace("\"EUR\"", "\"USD\""), "Taxi 64.80 USD", o => HasReason(o, "currency_mismatch"));
        Check("\"eur\" -> Invalid currency_not_iso (not silently upper-cased)",
            Sound.Replace("\"EUR\"", "\"eur\""), Taxi, o => HasReason(o, "currency_not_iso"));
        Check("amount 0 -> Invalid amount_not_positive",
            Sound.Replace("64.80", "0"), "Taxi 0 EUR", o => HasReason(o, "amount_not_positive"));
        Check("amount -64.80 -> Invalid amount_not_positive",
            Sound.Replace("64.80", "-64.80"), Taxi, o => HasReason(o, "amount_not_positive"));
        const string Hotel = "Hotel in Lyon, two nights: 90.00 + 90.00 EUR, total 190.00 EUR";
        const string HotelJson = """{"description":"Hotel","amount":190.00,"currency":"EUR","lines":[{"description":"Night 1","amount":90.00},{"description":"Night 2","amount":90.00}],"confidence":0.97}""";
        Check("lines 90 + 90 with a total of 190 -> Invalid lines_disagree_with_total", HotelJson, Hotel,
            o => HasReason(o, "lines_disagree_with_total"));
        Check("self-reported confidence 0.97 on that wrong total does not change the outcome", HotelJson, Hotel,
            o => o is Invalid && Validate(HotelJson.Replace("0.97", "0.10"), Hotel) is Invalid { Reasons.Count: 1 });
        Check("a line without an amount -> Invalid line_amount_missing",
            HotelJson.Replace("\"amount\":90.00}]", "\"amount\":null}]"), Hotel, o => HasReason(o, "line_amount_missing"));
        Check("\"lines\": null is read as no lines, not a crash",
            Sound.Replace("\"lines\":[]", "\"lines\":null"), Taxi, o => o is Valid);
        Check("6480 (64,80 read with the wrong culture) -> Invalid amount_not_in_source",
            Sound.Replace("64.80", "6480"), Taxi, o => HasReason(o, "amount_not_in_source"));
        Check("several broken rules are all named, not just the first",
            Sound.Replace("\"EUR\"", "\"USD\"").Replace("64.80", "-5"), Taxi, o => o is Invalid { Reasons.Count: 3 });
        Expect("source amounts: '1,275.50' -> 1275.50, '64,80' -> 64.80, '12 November' -> 12",
            ClaimValidator.SourceAmounts("1,275.50 and 64,80 on 12 November") is var a
            && a.Contains(1275.50m) && a.Contains(64.80m) && a.Contains(12m) && a.Count == 3);

        Console.WriteLine("\n-- The pipeline: TryRunStructuredAsync, then the ladder (fake chat client) --");
        await Pipeline("valid JSON -> Valid, one call", o => o is Valid, 1, CannedChatClient.Stop(Sound));
        await Pipeline("prose, then valid JSON -> Valid after one retry", o => o is Valid, 2,
            CannedChatClient.Stop("Sure! The taxi was 64,80 euros."), CannedChatClient.Stop(Sound));
        await Pipeline("prose twice -> Unreadable with the raw reply kept, two calls",
            o => o is Unreadable { Raw: "I cannot read this claim." }, 2, CannedChatClient.Stop("I cannot read this claim."));
        await Pipeline("\"amount\": \"64,80\" as a string twice -> Unreadable (strict number), two calls",
            o => o is Unreadable, 2, CannedChatClient.Stop(Sound.Replace("64.80", "\"64,80\"")));
        await Pipeline("empty reply twice -> Unreadable, not an exception", o => o is Unreadable, 2, CannedChatClient.Stop(""));
        await Pipeline("cut off at the output limit -> Truncated, no retry",
            o => o is Truncated { Raw: """{"description":"Taxi","amo""" }, 1, ("""{"description":"Taxi","amo""", ChatFinishReason.Length));
        await Pipeline("stopped by the content filter -> Refused, no retry", o => o is Refused, 1, ("", ChatFinishReason.ContentFilter));
        await Pipeline("valid shape, missing amount -> Missing, no retry (a retry is for shape only)",
            o => o is Missing, 1, CannedChatClient.Stop("""{"description":"Taxi","currency":"EUR"}"""));

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    private static async Task<AgentResponse<ExpenseClaim>> RunMinimalAsync(CannedChatClient client)
    {
        ChatClientAgent agent = new(client, instructions: ClaimPipeline.Instructions);
        AgentSession session = await agent.CreateSessionAsync();
        return await agent.RunAsync<ExpenseClaim>(ClaimPipeline.Prompt(Taxi), session, ClaimJson.Options);
    }

    private static async Task<ExpenseClaim> MinimalAsync(string json) =>
        (await RunMinimalAsync(new CannedChatClient(CannedChatClient.Stop(json)))).Result;

    private static ClaimOutcome Validate(string json, string source) =>
        ClaimValidator.Validate(JsonSerializer.Deserialize<ClaimDraft>(json, ClaimJson.Options)!, source, Rules);

    private static void Check(string name, string json, string source, Func<ClaimOutcome, bool> expected)
    {
        ClaimOutcome outcome = Validate(json, source);
        Expect($"{name}  [{Program.Describe(outcome)}]", expected(outcome));
    }

    private static async Task Pipeline(string name, Func<ClaimOutcome, bool> expected, int calls,
        params (string, ChatFinishReason)[] replies)
    {
        CannedChatClient client = new(replies);
        ChatClientAgent agent = new(client, instructions: ClaimPipeline.Instructions);
        ClaimOutcome outcome = await ClaimPipeline.ExtractAsync(agent, Taxi, Rules, NullLogger.Instance);
        Expect($"{name}  [{Program.Describe(outcome)}; {client.Calls} call(s)]", expected(outcome) && client.Calls == calls);
    }

    private static bool HasReason(ClaimOutcome o, string code) =>
        o is Invalid i && i.Reasons.Any(r => r.StartsWith(code, StringComparison.Ordinal));

    private static T UnderCulture<T>(string culture, Func<T> action)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static bool Throws<TException>(Func<object?> action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }

    private static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
