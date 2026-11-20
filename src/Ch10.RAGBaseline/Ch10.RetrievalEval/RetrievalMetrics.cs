namespace Ch10.RetrievalEval;

/// <summary>An annotated question: the chunk ids that answer it. None means "not in the handbook".</summary>
public sealed record EvalQuestion(string Id, string Question, string[] Relevant)
{
    public bool Answerable => Relevant.Length > 0;
}

/// <summary>One chunk of a ranking, best first.</summary>
public sealed record Ranked(string ChunkId, double Score);

/// <summary>Scores of one question at one K and one threshold.</summary>
public sealed record QuestionScore(EvalQuestion Question, IReadOnlyList<string> Retrieved, bool Hit, double Recall)
{
    public bool Abstained => Retrieved.Count == 0;
}

/// <summary>The set's scores at one K and one threshold.</summary>
public sealed record EvalReport(
    int K, double Threshold, double HitRate, double Recall,
    int CorrectAbstentions, int NoAnswerCount, int FalseAbstentions, int AnswerableCount,
    IReadOnlyList<QuestionScore> Questions)
{
    /// <summary>Answerable questions with a relevant chunk retrieved, plus no-answer questions that abstained.</summary>
    public int Correct => Questions.Count(q => q.Question.Answerable ? q.Hit : q.Abstained);
}

/// <summary>
/// Retrieval metrics. HitRate@K and Recall@K are different numbers:
/// HitRate@K asks "was at least one relevant chunk retrieved?",
/// Recall@K asks "which share of the relevant chunks was retrieved?".
/// They are equal only when every question has exactly one relevant chunk.
/// </summary>
public static class RetrievalMetrics
{
    /// <summary>The top K of a ranking, keeping only scores at or above the threshold.</summary>
    public static IReadOnlyList<string> Retrieve(IReadOnlyList<Ranked> ranking, int k, double threshold) =>
        [.. ranking.Take(k).Where(r => r.Score >= threshold).Select(r => r.ChunkId)];

    public static bool HitAtK(IEnumerable<string> relevant, IReadOnlyList<string> retrieved) =>
        relevant.Any(retrieved.Contains);

    public static double RecallAtK(IReadOnlyCollection<string> relevant, IReadOnlyList<string> retrieved) =>
        relevant.Count == 0 ? 0 : (double)relevant.Count(retrieved.Contains) / relevant.Count;

    /// <summary>
    /// HitRate@K and Recall@K are averaged over the answerable questions only;
    /// the no-answer questions are scored by whether retrieval returned nothing.
    /// </summary>
    public static EvalReport Evaluate(
        IReadOnlyList<EvalQuestion> questions,
        IReadOnlyDictionary<string, IReadOnlyList<Ranked>> rankings,
        int k, double threshold)
    {
        List<QuestionScore> scores = [];
        foreach (EvalQuestion q in questions)
        {
            IReadOnlyList<string> retrieved = Retrieve(rankings[q.Id], k, threshold);
            scores.Add(new QuestionScore(q, retrieved, HitAtK(q.Relevant, retrieved), RecallAtK(q.Relevant, retrieved)));
        }

        QuestionScore[] answerable = [.. scores.Where(s => s.Question.Answerable)];
        QuestionScore[] noAnswer = [.. scores.Where(s => !s.Question.Answerable)];
        return new EvalReport(
            k, threshold,
            HitRate: answerable.Length == 0 ? 0 : answerable.Average(s => s.Hit ? 1.0 : 0.0),
            Recall: answerable.Length == 0 ? 0 : answerable.Average(s => s.Recall),
            CorrectAbstentions: noAnswer.Count(s => s.Abstained), NoAnswerCount: noAnswer.Length,
            FalseAbstentions: answerable.Count(s => s.Abstained), AnswerableCount: answerable.Length,
            scores);
    }

    /// <summary>
    /// The threshold with the most correct outcomes (hits on answerable questions
    /// plus abstentions on no-answer questions). On a tie this picks the lowest,
    /// which keeps the most evidence; the report prints the whole tied range,
    /// because a small set cannot tell the thresholds inside it apart.
    /// </summary>
    public static EvalReport Choose(IEnumerable<EvalReport> sweep) =>
        sweep.OrderByDescending(r => r.Correct).ThenBy(r => r.Threshold).First();
}
