// ============================================================================
// Token Explorer: tokens, context budgets, sampling and similarity, offline.
// Every number below comes from a real tokenizer or from plain arithmetic;
// nothing here calls a model.
//
//   dotnet run                 the tour
//   dotnet run -- --self-test  fixed token counts and the budget rules, RESULT n/n
// ============================================================================

using System.Text;
using Ch02.TokenExplorer;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

Console.OutputEncoding = Encoding.UTF8;

if (args.Contains("--self-test")) return TokenSelfTest.Run();

Console.WriteLine("=== Token Explorer ===");

// ----------------------------------------------------------------------------
#region Estimate, then verify
// ----------------------------------------------------------------------------
// Two tokenizer families from Microsoft.ML.Tokenizers, both offline: the vocab
// ships in the Data.O200kBase and Data.Cl100kBase packages. Which vendor model
// uses which encoding is a dated fact; check the vendor's documentation.

WriteSection("Estimate, then verify");

Tokenizer o200k = TiktokenTokenizer.CreateForEncoding("o200k_base");
Tokenizer cl100k = TiktokenTokenizer.CreateForEncoding("cl100k_base");

Console.WriteLine($"  {"Sample",-9} {"Chars",6} {"len/4",6} {"o200k",6} {"cl100k",7} {"chars/token",12} {"len/4 error",12}");
Console.WriteLine($"  {new string('-', 64)}");
foreach (Sample sample in Samples.All)
{
    int estimate = TokenMath.EstimateTokens(sample.Text);
    int real = o200k.CountTokens(sample.Text);
    int older = cl100k.CountTokens(sample.Text);
    double ratio = (double)sample.Text.Length / real;
    double error = (double)(estimate - real) / real;
    Console.WriteLine($"  {sample.Label,-9} {sample.Text.Length,6} {estimate,6} {real,6} {older,7} {ratio,12:F2} {error,12:P0}");
}

Console.WriteLine();
Console.WriteLine("  len/4 overcounts the English and C# samples and undercounts the Arabic one,");
Console.WriteLine("  and cl100k_base needs about twice the tokens of o200k_base for the Arabic.");
Console.WriteLine("  Estimate to plan; count with the model's own tokenizer before you send.");

#endregion

// ----------------------------------------------------------------------------
#region The conversation is a list of messages
// ----------------------------------------------------------------------------
// A chat request is an ordered List<ChatMessage>, re-sent in full on every
// turn. Counting the whole list each turn shows the history riding along.

WriteSection("The conversation is a list of messages");

List<ChatMessage> conversation =
[
    new(ChatRole.System, "You answer Contoso expense-policy questions in one sentence."),
];

(string User, string Assistant)[] turns =
[
    ("Can I claim a 42 EUR taxi?", "Yes: claims up to 50 EUR need no review."),
    ("And 64.80 EUR?", "That needs a finance reviewer's approval first."),
    ("Can I approve it myself?", "No. The reviewer must be someone other than the requester."),
    ("What if my account is in EUR and the receipt is in USD?", "The claim is denied: the currency must match the account."),
];

Console.WriteLine($"  {"Turn",-5} {"Messages",9} {"Input tokens (o200k)",21}");
Console.WriteLine($"  {new string('-', 37)}");
int turn = 0;
foreach ((string user, string assistant) in turns)
{
    conversation.Add(new(ChatRole.User, user));
    Console.WriteLine($"  {++turn,-5} {conversation.Count,9} {TokenMath.CountMessages(conversation, o200k),21}");
    conversation.Add(new(ChatRole.Assistant, assistant));
}

Console.WriteLine();
Console.WriteLine("  The per-message framing is an approximation (4 tokens); providers differ.");

#endregion

// ----------------------------------------------------------------------------
#region Context budget
// ----------------------------------------------------------------------------
// The window holds the input AND the output. Plan the parts, keep a reserve
// for the answer, and refuse a request that does not fit before calling.

WriteSection("Context budget");

ContextBudget plan = new(
    Window: 8_192, Instructions: 600, History: 2_400,
    Evidence: 2_000, ToolResults: 800, OutputReserve: 1_024);
PrintBudget("Planned", plan);
PrintBudget("History grew", plan with { History = 4_200 });

Console.WriteLine();
Console.WriteLine("  Send OutputReserve as ChatOptions.MaxOutputTokens. A reply that hits");
Console.WriteLine("  the cap ends with FinishReason Length: the output was cut, not finished.");

#endregion

// ----------------------------------------------------------------------------
#region Temperature and top-p (a toy distribution)
// ----------------------------------------------------------------------------
// Temperature rescales the next-token distribution before sampling. This is
// arithmetic on made-up probabilities, not a model: a provider may clamp,
// ignore or reinterpret the value, and temperature 0 is not a determinism
// guarantee (batching and hardware can still change the result).

WriteSection("Temperature and top-p (a toy distribution)");

Dictionary<string, double> next = new()
{
    ["approved"] = 0.45, ["pending"] = 0.25, ["denied"] = 0.20, ["escalated"] = 0.10,
};
Console.WriteLine("  \"The claim is ___\"");
foreach (double t in new[] { 0.2, 0.7, 1.0, 1.5 })
{
    Dictionary<string, double> scaled = TokenMath.ApplyTemperature(next, t);
    Console.WriteLine($"  T={t:F1}  " + string.Join("  ", scaled.Select(kv => $"{kv.Key} {kv.Value:P0}")));
}
string[] nucleus = TokenMath.TopP(next, 0.7);
Console.WriteLine($"  top-p 0.7 keeps: {string.Join(", ", nucleus)}");

#endregion

// ----------------------------------------------------------------------------
#region Cosine similarity
// ----------------------------------------------------------------------------
// Made-up 4-dimension vectors; a real embedding model returns hundreds of
// dimensions. The retrieval chapter computes real ones.

WriteSection("Cosine similarity (toy vectors)");

float[] taxi = [0.9f, 0.1f, 0.3f, 0.0f];
float[] cab = [0.8f, 0.2f, 0.3f, 0.1f];
float[] lunch = [0.1f, 0.9f, 0.2f, 0.4f];
Console.WriteLine($"  taxi receipt vs cab fare:      {TokenMath.Cosine(taxi, cab):F3}");
Console.WriteLine($"  taxi receipt vs team lunch:    {TokenMath.Cosine(taxi, lunch):F3}");

#endregion

Console.WriteLine("\n=== Token Explorer complete ===");
return 0;

static void PrintBudget(string label, ContextBudget budget)
{
    string verdict = budget.Fits
        ? $"fits, {budget.Free:N0} free"
        : $"OVER by {-budget.Free:N0}: trim before the call";
    Console.WriteLine($"  {label,-13} input {budget.Input,6:N0} + reserve {budget.OutputReserve,5:N0} of {budget.Window:N0} -> {verdict}");
}

static void WriteSection(string title) => Console.WriteLine($"\n--- {title} ---\n");
