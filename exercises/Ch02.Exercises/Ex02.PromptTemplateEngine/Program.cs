// ============================================================================
// Exercise 02: Prompt Template Engine (Intermediate)
// {placeholder} templates rendered under a token budget: a missing variable
// fails loudly, a prompt that would not leave the output reserve free is
// rejected before any model call, and the reserve becomes the output cap.
//
//   dotnet run                  three templates, then a blown budget
//   dotnet run -- --self-test   the three rules, RESULT n/n
// ============================================================================

using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

PromptTemplate system = new("SystemPrompt", """
    You are {assistant_name}, the expense-policy assistant for {company}.
    Answer only from the policy text you are given. If it does not say, say so.
    """);
PromptTemplate question = new("UserQuestion", """
    Employee tier: {tier}
    Question: {question}
    """);
PromptTemplate fewShot = new("FewShotClassifier", """
    Classify the claim as allow, review or deny.
    Claim: "Taxi, 42.00 EUR, EUR account" -> allow
    Claim: "Taxi, 64.80 EUR, EUR account" -> review
    Claim: "{claim}" ->
    """);

if (args.Contains("--self-test")) return SelfTest(tokenizer, question);

// A small window on purpose, so the budget is easy to blow.
PromptBudget budget = new(Window: 160, OutputReserve: 64);
Console.WriteLine($"Budget: window {budget.Window}, output reserve {budget.OutputReserve}, prompt limit {budget.PromptLimit} (o200k_base)\n");

Show(system.Render(new() { ["assistant_name"] = "Contoso Assistant", ["company"] = "Contoso" }, budget, tokenizer));
Show(question.Render(new() { ["tier"] = "standard", ["question"] = "Can I claim a 42 EUR taxi?" }, budget, tokenizer));
Show(fewShot.Render(new() { ["claim"] = "Hotel, 180.00 USD, EUR account" }, budget, tokenizer));

string pasted = string.Join(' ', Enumerable.Repeat("Here is the whole receipt text I copied from the scan:", 12));
try
{
    question.Render(new() { ["tier"] = "standard", ["question"] = pasted }, budget, tokenizer);
}
catch (PromptOverBudgetException ex)
{
    Console.WriteLine($"Rejected before the call: {ex.Message}");
}
try
{
    question.Render(new() { ["tier"] = "standard" }, budget, tokenizer);
}
catch (KeyNotFoundException ex)
{
    Console.WriteLine($"Rejected before the call: {ex.Message}");
}
return 0;

static void Show(RenderedPrompt p) =>
    Console.WriteLine($"[{p.Name}] {p.Tokens} tokens, MaxOutputTokens {p.MaxOutputTokens}\n{p.Text}\n");

static int SelfTest(Tokenizer tokenizer, PromptTemplate question)
{
    int passed = 0, total = 0;
    void Expect(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    PromptBudget budget = new(Window: 160, OutputReserve: 64);
    int calls = 0;
    void Send(RenderedPrompt _) => calls++;   // stands in for the model call

    RenderedPrompt ok = question.Render(new() { ["tier"] = "standard", ["question"] = "Can I claim a taxi?" }, budget, tokenizer);
    Send(ok);
    Expect("a prompt inside the limit renders and is sent", calls == 1 && ok.Tokens <= budget.PromptLimit);
    Expect("the output reserve becomes MaxOutputTokens", ok.MaxOutputTokens == 64);
    Expect("the count is the tokenizer's, not len/4", ok.Tokens == tokenizer.CountTokens(ok.Text));

    bool missing = Throws<KeyNotFoundException>(() => Send(question.Render(new() { ["tier"] = "standard" }, budget, tokenizer)));
    Expect("a missing variable throws and nothing is sent", missing && calls == 1);

    string pasted = string.Join(' ', Enumerable.Repeat("Here is the whole receipt text I copied from the scan:", 12));
    bool over = Throws<PromptOverBudgetException>(() => Send(question.Render(new() { ["tier"] = "standard", ["question"] = pasted }, budget, tokenizer)));
    Expect("an over-budget prompt is rejected and nothing is sent", over && calls == 1);

    // Fits the window on its own, but not with the reserve: still rejected.
    string medium = string.Join(' ', Enumerable.Repeat("receipt line", 55));
    int alone = tokenizer.CountTokens(question.Template.ReplaceLineEndings("\n").Replace("{tier}", "standard").Replace("{question}", medium));
    bool reserve = Throws<PromptOverBudgetException>(() => question.Render(new() { ["tier"] = "standard", ["question"] = medium }, budget, tokenizer));
    Expect("a prompt that fits the window but eats the reserve is rejected",
        alone < budget.Window && alone > budget.PromptLimit && reserve);

    Expect("a reserve as large as the window is refused", Throws<ArgumentOutOfRangeException>(() => _ = new PromptBudget(100, 100)));

    Console.WriteLine($"\nRESULT {passed}/{total}");
    return passed == total ? 0 : 1;
}

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
}

/// <summary>The window and the part of it kept free for the reply.</summary>
sealed record PromptBudget
{
    public PromptBudget(int Window, int OutputReserve)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OutputReserve);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(OutputReserve, Window);
        this.Window = Window;
        this.OutputReserve = OutputReserve;
    }

    public int Window { get; }
    public int OutputReserve { get; }
    public int PromptLimit => Window - OutputReserve;
}

sealed record RenderedPrompt(string Name, string Text, int Tokens, int MaxOutputTokens);

sealed class PromptOverBudgetException(string message) : Exception(message);

/// <summary>A template with {placeholder} variables.</summary>
sealed partial record PromptTemplate(string Name, string Template)
{
    public RenderedPrompt Render(Dictionary<string, string> values, PromptBudget budget, Tokenizer tokenizer)
    {
        // Normalized so a CRLF checkout renders (and counts) the same as LF.
        string text = Placeholder().Replace(Template.ReplaceLineEndings("\n"), m =>
            values.TryGetValue(m.Groups[1].Value, out string? v)
                ? v
                : throw new KeyNotFoundException($"Template '{Name}' needs '{m.Groups[1].Value}', which was not given."));

        int tokens = tokenizer.CountTokens(text);
        if (tokens > budget.PromptLimit)
            throw new PromptOverBudgetException(
                $"'{Name}' is {tokens} tokens; the limit is {budget.PromptLimit} " +
                $"({budget.Window} window - {budget.OutputReserve} output reserve).");

        return new(Name, text, tokens, budget.OutputReserve);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
