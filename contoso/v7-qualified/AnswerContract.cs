using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Contoso.Qualified;

/// <summary>
/// How one desk shows evidence to the model and how an answer cites it. Supplied ids are
/// read from headers at the start of a line, the way the desk writes them; text inside the
/// evidence cannot add an id mid-line.
/// </summary>
public sealed class EvidenceFormat(Regex header, Regex cited, Regex idLike, string example)
{
    /// <summary>"[doc@vN#P] text": the evidence cases and Ch11's EvidenceAgent.Format; cited as [doc@vN#P].</summary>
    public static readonly EvidenceFormat Versioned = new(
        new(@"^\[(?<id>[a-z0-9-]+@v\d+#\d+)\]", RegexOptions.Multiline),
        new(@"\[(?<id>[a-z0-9-]+@v\d+#\d+)\]"),
        new(@"[a-z0-9-]+@v\d+#\d+", RegexOptions.IgnoreCase),
        "[pto-policy@v1#2]");

    /// <summary>"[Source: x]" blocks: Ch10's FaqContextProvider.FormatEvidence; cited as [x], the rule of v3's FaqDesk.</summary>
    public static readonly EvidenceFormat Handbook = new(
        new(@"^\[Source: (?<id>[a-z0-9-]+)\]", RegexOptions.Multiline),
        new(@"\[(?:Source:\s*)?(?<id>[a-z0-9-]+)\]"),
        new(@"\[(?:Source:\s*)?(?<id>[^\]\s][^\]]*)\]"),
        "[expense-policy]");

    public string Example => example;

    /// <summary>Every evidence block in the request: its id and its text, up to the next header or blank line.</summary>
    public IReadOnlyList<(string Id, string Text)> Supplied(IEnumerable<string> texts)
    {
        List<(string, string)> blocks = [];
        foreach (string text in texts)
        {
            Match[] heads = [.. header.Matches(text).Cast<Match>()];
            for (int i = 0; i < heads.Length; i++)
            {
                int end = i + 1 < heads.Length ? heads[i + 1].Index : text.Length;
                int blank = text.IndexOf("\n\n", heads[i].Index, end - heads[i].Index, StringComparison.Ordinal);
                blocks.Add((heads[i].Groups["id"].Value, text[heads[i].Index..(blank >= 0 ? blank : end)]));
            }
        }
        return blocks;
    }

    public IReadOnlyList<string> Cited(string answer) => [.. cited.Matches(answer).Select(m => m.Groups["id"].Value).Distinct()];

    /// <summary>Every id-shaped token, bracketed or not, in any case: anything here that was not supplied blocks the answer.</summary>
    public IReadOnlyList<string> IdLike(string answer) =>
        [.. idLike.Matches(answer).Select(m => (m.Groups["id"].Success ? m.Groups["id"].Value : m.Value).ToLowerInvariant()).Distinct()];
}

/// <summary>What the contract decided about one answer.</summary>
public sealed record ContractVerdict(bool Accepted, string Text, string Reason);

/// <summary>One answer as the contract handled it, for the run's log.</summary>
public sealed record ContractRecord(string Outcome, int ModelCalls, string Reason);

/// <summary>
/// The answer contract: every stated value carries the id of a supplied evidence block, checked
/// by code after generation. A value sentence with no citation gets the id of the one supplied
/// block that holds all its values, when exactly one does; otherwise the model is asked once
/// more, without tools; an answer that still fails is replaced by the desk's abstention.
/// An uncited answer is never returned as an answer.
/// </summary>
public static partial class AnswerContract
{
    /// <summary>The one retry's message. {0}: why the answer was refused; {1}: an example id.</summary>
    public const string RetryInstruction = """
        Your last answer was not accepted: {0}.
        Answer again, from the evidence above only. Put the id of the evidence that holds each fact
        in square brackets right after the sentence that states it, for example {1}.
        If the evidence does not answer the question, give only the refusal sentence from your instructions.
        """;

    public static ContractVerdict Check(string answer, IReadOnlyList<(string Id, string Text)> supplied,
        EvidenceFormat format, IReadOnlyList<string> abstentions)
    {
        HashSet<string> ids = [.. supplied.Select(s => s.Id)];
        string[] foreign = [.. format.IdLike(answer).Where(id => !ids.Contains(id))];
        IReadOnlyList<string> cited = format.Cited(answer);
        string[] values = [.. Values(answer)];
        bool abstains = abstentions.Any(a => answer.Replace('’', '\'').Contains(a, StringComparison.OrdinalIgnoreCase));

        if (foreign.Length > 0) return new(false, answer, $"it cites {string.Join(", ", foreign)}, which this request was not given");
        if (abstains)
            return values.Length == 0 && cited.Count == 0 ? new(true, answer, "abstained")
                : new(false, answer, "it refuses and still states a value or cites evidence");
        if (ids.Count == 0)
            return values.Length == 0 && cited.Count == 0 ? new(true, answer, "no evidence, and no value stated")
                : new(false, answer, $"it states {string.Join(", ", values)} with no evidence supplied");
        if (cited.Count == 0) return new(false, answer, "it cites no evidence id");
        string[] uncited = [.. Sentences(answer, format).Where(s => !s.Cited && Values(s.Text).Any()).Select(s => s.Text.Trim())];
        return uncited.Length == 0 ? new(true, answer, "every stated value is cited")
            : new(false, answer, $"\"{Short(uncited[0])}\" states a value with no citation");
    }

    /// <summary>
    /// Deterministic citations: a value sentence with none gets the id of the only supplied block
    /// that holds every value it states. Ambiguous or unmatched sentences are left as they are.
    /// </summary>
    public static string Attach(string answer, IReadOnlyList<(string Id, string Text)> supplied, EvidenceFormat format)
    {
        if (format.IdLike(answer).Any(id => supplied.All(s => s.Id != id))) return answer;
        List<Sentence> sentences = Sentences(answer, format);
        System.Text.StringBuilder sb = new();
        foreach (Sentence s in sentences)
        {
            string[] values = [.. Values(s.Text)];
            string[] holders = s.Cited || values.Length == 0 ? []
                : [.. supplied.Where(b => values.All(new HashSet<string>(Values(b.Text)).Contains)).Select(b => b.Id).Distinct()];
            if (holders.Length != 1) { sb.Append(s.Text).Append(s.Separator); continue; }
            string t = s.Text.TrimEnd();
            string tail = s.Text[t.Length..];
            sb.Append(t.Length > 0 && ".!?".Contains(t[^1]) ? $"{t[..^1]} [{holders[0]}]{t[^1]}" : $"{t} [{holders[0]}]")
              .Append(tail).Append(s.Separator);
        }
        return sb.ToString();
    }

    /// <summary>Check, then attach and check again.</summary>
    public static ContractVerdict Apply(string answer, IReadOnlyList<(string Id, string Text)> supplied,
        EvidenceFormat format, IReadOnlyList<string> abstentions)
    {
        ContractVerdict first = Check(answer, supplied, format, abstentions);
        if (first.Accepted) return first;
        string attached = Attach(answer, supplied, format);
        if (attached == answer) return first;
        ContractVerdict second = Check(attached, supplied, format, abstentions);
        return second.Accepted ? second with { Reason = "citations attached by code" } : first;
    }

    /// <summary>The values an answer states: numbers outside identifiers and ranges, and number words before a unit.</summary>
    public static IEnumerable<string> Values(string text) =>
        Number().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ':'))
            .Concat(WordValue().Matches(text).Select(m => (Array.IndexOf(Words, m.Groups["w"].Value.ToLowerInvariant()) + 1)
                .ToString(CultureInfo.InvariantCulture)))
            .Distinct();

    private static readonly string[] Words = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty"];

    private sealed record Sentence(string Text, string Separator, bool Cited);

    /// <summary>Sentences and list lines; a piece that holds only citations belongs to the sentence before it.</summary>
    private static List<Sentence> Sentences(string text, EvidenceFormat format)
    {
        string[] parts = Splitter().Split(text);
        List<(string Text, string Sep)> pieces = [];
        for (int i = 0; i < parts.Length; i += 2) pieces.Add((parts[i], i + 1 < parts.Length ? parts[i + 1] : ""));
        List<Sentence> result = [];
        for (int i = 0; i < pieces.Count; i++)
        {
            bool next = i + 1 < pieces.Count && OnlyCitations(pieces[i + 1].Text, format);
            result.Add(new(pieces[i].Text, pieces[i].Sep, format.Cited(pieces[i].Text).Count > 0 || next));
        }
        return result;
    }

    private static bool OnlyCitations(string piece, EvidenceFormat format) =>
        format.Cited(piece).Count > 0 && CitationToken().Replace(piece, "").Trim(' ', '.', ',', ';', '\t').Length == 0;

    private static string Short(string s) => s.Length > 60 ? s[..60] + "..." : s;

    [GeneratedRegex(@"((?<=[.!?])\s+|\n+)")] private static partial Regex Splitter();
    [GeneratedRegex(@"\[[^\]]*\]")] private static partial Regex CitationToken();
    [GeneratedRegex(@"(?<![\w@#.:/-])\d+(?:[.,:]\d+)*(?![\w@#/-])")] private static partial Regex Number();
    [GeneratedRegex(@"\b(?<w>one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty)\s+(?:[a-z]+\s+)?(?:days?|weeks?|months?|years?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordValue();
}

/// <summary>
/// The answer contract as a chat-client layer, so a desk built around an IChatClient gets it
/// without changing: a pending tool call passes through; a structured (JSON) response is the
/// action policy's to check; every prose answer is checked, given one retry, or replaced.
/// </summary>
public sealed class AnswerContractClient(IChatClient inner, EvidenceFormat format, IReadOnlyList<string> abstentions, string abstention)
    : DelegatingChatClient(inner)
{
    private readonly ConcurrentQueue<ContractRecord> _log = new();

    /// <summary>Every answer the contract handled, in the order they finished.</summary>
    public IReadOnlyList<ContractRecord> Log => [.. _log];

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatMessage[] request = [.. messages];
        ChatResponse first = await base.GetResponseAsync(request, options, cancellationToken);
        if (options?.ResponseFormat is ChatResponseFormatJson
            || first.Messages.LastOrDefault()?.Contents.OfType<FunctionCallContent>().Any() == true) return first;

        IReadOnlyList<(string Id, string Text)> supplied =
            format.Supplied([.. request.Where(m => m.Role != ChatRole.Assistant).Select(m => m.Text), options?.Instructions ?? ""]);
        ContractVerdict v = AnswerContract.Apply(first.Text, supplied, format, abstentions);
        if (v.Accepted) return Done(first, v.Text, v.Reason == "citations attached by code" ? "attached" : "accepted", 1, v.Reason);

        // One bounded retry: the same request, the refused answer, and why. No tools: it only rewrites.
        ChatOptions? again = options?.Clone();
        if (again is not null) { again.Tools = null; again.ToolMode = null; }
        string retry = string.Format(CultureInfo.InvariantCulture, AnswerContract.RetryInstruction, v.Reason,
            supplied.Count > 0 ? $"[{supplied[0].Id}]" : format.Example);
        ChatResponse second = await base.GetResponseAsync(
            [.. request, .. first.Messages, new ChatMessage(ChatRole.User, retry)], again, cancellationToken);
        ContractVerdict w = AnswerContract.Apply(second.Text, supplied, format, abstentions);
        if (w.Accepted) return Done(second, w.Text, w.Reason == "citations attached by code" ? "attached after retry" : "accepted after retry", 2, v.Reason);

        // Still not backed: an action turn shows what the tools returned; anything else abstains.
        string[] results = [.. request.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => Convert.ToString(r.Result, CultureInfo.InvariantCulture) ?? "")];
        return results.Length > 0
            ? Done(second, string.Join(" ", results), "tool result shown", 2, $"{v.Reason}; then {w.Reason}")
            : Done(second, abstention, "abstained", 2, $"{v.Reason}; then {w.Reason}");
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates()) yield return update;
    }

    private ChatResponse Done(ChatResponse r, string text, string outcome, int calls, string reason)
    {
        _log.Enqueue(new ContractRecord(outcome, calls, reason));
        if (text == r.Text) return r;
        return new ChatResponse([.. r.Messages.Take(Math.Max(0, r.Messages.Count - 1)), new ChatMessage(ChatRole.Assistant, text)])
        {
            ResponseId = r.ResponseId, ModelId = r.ModelId, FinishReason = r.FinishReason, Usage = r.Usage, CreatedAt = r.CreatedAt,
        };
    }
}
