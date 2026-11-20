using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ch02.TokenExplorer;

/// <summary>One text the explorer counts.</summary>
public sealed record Sample(string Label, string Text);

public static class Samples
{
    // The same policy rule three ways: English prose, Arabic prose and C#.
    public static readonly Sample English = new("English",
        "Contoso reimburses taxi fares up to 50 EUR without review; larger claims need a finance reviewer.");

    public static readonly Sample Arabic = new("Arabic",
        "تعوّض كونتوسو أجرة سيارة الأجرة حتى 50 يورو دون مراجعة؛ وتحتاج المطالبات الأكبر إلى مراجع مالي.");

    // Joined with "\n" so the count is the same whatever line endings git checks out.
    public static readonly Sample CSharp = new("C#", string.Join('\n',
        "if (proposal.Amount <= LowValueLimit)",
        "    return new(Decision.Allow, \"low_value_policy\");",
        "if (approval is null)",
        "    return new(Decision.RequireApproval, \"approval_missing\");"));

    public static readonly Sample[] All = [English, Arabic, CSharp];
}

/// <summary>
/// The parts of one request, in tokens. The window must hold the input and
/// the reply, so the output reserve is planned like any other part.
/// </summary>
public sealed record ContextBudget(
    int Window, int Instructions, int History, int Evidence, int ToolResults, int OutputReserve)
{
    public int Input => Instructions + History + Evidence + ToolResults;
    public int Free => Window - Input - OutputReserve;
    public bool Fits => Free >= 0;
}

public static class TokenMath
{
    /// <summary>The length / 4 rule of thumb. An estimate, not a tokenizer.</summary>
    public static int EstimateTokens(string text) =>
        text.Length == 0 ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    /// <summary>
    /// Tokens for a whole message list. The per-message framing (role and
    /// delimiters) differs by provider; 4 is an approximation.
    /// </summary>
    public static int CountMessages(IEnumerable<ChatMessage> messages, Tokenizer tokenizer, int framing = 4) =>
        messages.Sum(m => framing + tokenizer.CountTokens(m.Text));

    /// <summary>Softmax over log-probabilities divided by the temperature.</summary>
    public static Dictionary<string, double> ApplyTemperature(Dictionary<string, double> probs, double temperature)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(temperature);
        Dictionary<string, double> scaled = probs.ToDictionary(kv => kv.Key, kv => Math.Exp(Math.Log(kv.Value) / temperature));
        double sum = scaled.Values.Sum();
        return scaled.ToDictionary(kv => kv.Key, kv => kv.Value / sum);
    }

    /// <summary>The smallest set of most likely tokens whose mass reaches p.</summary>
    public static string[] TopP(Dictionary<string, double> probs, double p)
    {
        List<string> kept = [];
        double mass = 0;
        foreach ((string token, double prob) in probs.OrderByDescending(kv => kv.Value))
        {
            kept.Add(token);
            mass += prob;
            if (mass >= p) break;
        }
        return [.. kept];
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
