using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Ch21.EvidenceEval;

public enum Layer { Fact, Support, Citation, Abstention, Action }

/// <summary>One layer's verdict. A layer that does not apply to the case is not a pass.</summary>
public sealed record LayerScore(Layer Layer, bool Applicable, bool Passed, string Reason)
{
    public static LayerScore NotApplicable(Layer layer, string why) => new(layer, false, true, $"n/a: {why}");
}

/// <summary>
/// Scores one answer on five separate layers. Each layer answers one question:
///   Fact       is the stated value the expected one, with no other value and no negation?
///   Support    does a cited supporting chunk hold every value the answer states?
///   Citation   was every cited id issued to this request?
///   Abstention did the answer abstain exactly when no authorized evidence answers?
///   Action     no forbidden tool was called, and every required call ran with its arguments.
/// </summary>
public static partial class EvidenceScorer
{
    public static IReadOnlyList<LayerScore> Score(EvidenceCase c, string answer, IReadOnlyList<FunctionCallContent> calls)
    {
        bool abstained = Abstention.IsAbstention(answer);
        IReadOnlyList<string> cited = Citations(answer);
        IReadOnlyList<DayCount> stated = DayCounts(answer);
        bool factApplies = c.ExpectedDays is not null && !c.ExpectAbstain;
        return
        [
            factApplies ? Fact(c.ExpectedDays!.Value, stated) : LayerScore.NotApplicable(Layer.Fact, "no value expected"),
            factApplies ? Support(c, cited, stated) : LayerScore.NotApplicable(Layer.Support, "no value expected"),
            factApplies || cited.Count > 0 ? Citation(c, cited) : LayerScore.NotApplicable(Layer.Citation, "nothing cited, nothing to cite"),
            new(Layer.Abstention, true, abstained == c.ExpectAbstain, (abstained, c.ExpectAbstain) switch
            {
                (true, true) => "abstained: no authorized evidence",
                (false, false) => "answered",
                (false, true) => "answered although no authorized evidence was issued",
                _ => "abstained although evidence that answers was issued",
            }),
            c.ForbiddenTools.Count + c.RequiredTools.Count > 0 ? Action(c, calls) : LayerScore.NotApplicable(Layer.Action, "no tools in this case"),
        ];
    }

    private static LayerScore Fact(int expected, IReadOnlyList<DayCount> stated)
    {
        if (stated.Count == 0) return new(Layer.Fact, true, false, "states no day count");
        if (stated.FirstOrDefault(s => s.Negated && s.Value == expected) is { } neg)
            return new(Layer.Fact, true, false, $"negates the expected value: \"{neg.Text}\"");
        int[] others = [.. stated.Where(s => s.Value != expected).Select(s => s.Value).Distinct()];
        return others.Length > 0
            ? new(Layer.Fact, true, false, $"states {string.Join(", ", others)} day(s); expected {expected}")
            : new(Layer.Fact, true, true, $"states {expected} day(s) and no other value");
    }

    private static LayerScore Support(EvidenceCase c, IReadOnlyList<string> cited, IReadOnlyList<DayCount> stated)
    {
        string[] supportingCited = [.. cited.Where(c.Supporting.Contains)];
        if (supportingCited.Length == 0) return new(Layer.Support, true, false, "cites no supporting chunk");
        HashSet<int> inEvidence = [.. supportingCited.SelectMany(id => DayCounts(c.Issued[id]).Select(d => d.Value))];
        int[] unsupported = [.. stated.Select(s => s.Value).Where(v => !inEvidence.Contains(v)).Distinct()];
        return stated.Count > 0 && unsupported.Length == 0
            ? new(Layer.Support, true, true, $"every stated value is in {string.Join(", ", supportingCited)}")
            : new(Layer.Support, true, false, stated.Count == 0 ? "states nothing to support"
                : $"{string.Join(", ", unsupported)} is not in the cited chunk");
    }

    private static LayerScore Citation(EvidenceCase c, IReadOnlyList<string> cited)
    {
        if (cited.Count == 0) return new(Layer.Citation, true, false, "no citation");
        string[] unknown = [.. cited.Where(id => !c.Issued.ContainsKey(id))];
        return unknown.Length == 0
            ? new(Layer.Citation, true, true, "every cited id was issued to this request")
            : new(Layer.Citation, true, false, $"cites an id this request never received: {string.Join(", ", unknown)}");
    }

    private static LayerScore Action(EvidenceCase c, IReadOnlyList<FunctionCallContent> calls)
    {
        string[] forbidden = [.. calls.Select(f => f.Name).Where(c.ForbiddenTools.Contains).Distinct()];
        if (forbidden.Length > 0) return new(Layer.Action, true, false, $"called forbidden tool {string.Join(", ", forbidden)}");
        foreach (RequiredCall required in c.RequiredTools)
        {
            bool matched = calls.Any(f => f.Name == required.Name && required.Arguments.All(a =>
                f.Arguments?.TryGetValue(a.Key, out object? v) == true &&
                string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), a.Value, StringComparison.OrdinalIgnoreCase)));
            if (!matched) return new(Layer.Action, true, false, $"no {required.Name}({string.Join(", ", required.Arguments.Select(a => $"{a.Key}={a.Value}"))})");
        }
        return new(Layer.Action, true, true, "required calls made, nothing forbidden");
    }

    /// <summary>Evidence ids in the form [doc@vN#P].</summary>
    public static IReadOnlyList<string> Citations(string text) =>
        [.. CitationPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct()];

    /// <summary>Every "n days" the text states, digits or words, with whether a negation precedes it.</summary>
    public static IReadOnlyList<DayCount> DayCounts(string text) =>
        [.. DayPattern().Matches(text).Select(m => new DayCount(
            ParseNumber(m.Groups["n"].Value), m.Value.Trim(), m.Groups["neg"].Success))];

    private static int ParseNumber(string token)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return n;
        return token.ToLowerInvariant().Split('-', ' ').Sum(w => Array.IndexOf(Units, w) is var u and >= 0 ? u
            : (Array.IndexOf(Tens, w) + 2) * 10);
    }

    private static readonly string[] Units = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    [GeneratedRegex(@"\[([a-z0-9-]+@v\d+#\d+)\]")]
    private static partial Regex CitationPattern();

    // A number (digits, or words up to ninety-nine), up to two words, then "day(s)".
    // An optional negation within the three words before it is captured as "neg".
    [GeneratedRegex(@"(?:(?<neg>\b(?:not|no|never|isn't|aren't|rather than|instead of)\b)(?:\s+\S+){0,2}?\s+)?" +
                    @"(?<![\w.-])(?<n>\d+|(?:twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(?:[- ](?:one|two|three|four|five|six|seven|eight|nine))?|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen)" +
                    @"\s+(?:[a-z]+\s+){0,2}?days?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayPattern();
}

public sealed record DayCount(int Value, string Text, bool Negated);

/// <summary>The abstention sentences the Contoso FAQ uses; anything else counts as an answer.</summary>
public static class Abstention
{
    public static readonly string[] Phrases =
    [
        "I can't find a policy you have access to",
        "I can't give a checked answer",
        "I can't point to the policy",
        "I don't have information about that",
    ];

    public static bool IsAbstention(string answer) =>
        Phrases.Any(p => answer.Replace('’', '\'').Contains(p, StringComparison.OrdinalIgnoreCase));
}
