using ModelContextProtocol;

namespace Ch09.KnowledgeBaseMcp;

/// <summary>Offline check: dotnet run -- --self-test. The five documents in docs/, no server, no model.</summary>
internal static class KnowledgeBaseSelfTest
{
    static int s_passed, s_total;

    public static int Run()
    {
        Console.WriteLine("=== Knowledge base: self-test ===");
        IReadOnlyList<KbDocument> docs = KnowledgeBaseStore.Load();
        Expect("five documents load from docs/", docs.Count == 5, $"documents: {string.Join(", ", docs.Select(d => d.Name))}");

        Console.WriteLine("\n-- Keyword matcher --");
        IReadOnlyList<string> terms = KeywordMatcher.Terms("How do I work remotely, and is a laptop provided?");
        Expect("terms under 3 characters are dropped, the rest lower-cased",
            !terms.Contains("do") && !terms.Contains("i") && !terms.Contains("a") && terms.Contains("laptop"),
            $"terms: {string.Join(" ", terms)}");

        IReadOnlyList<KbMatch> remote = KeywordMatcher.Search(docs, "remote work laptop");
        Expect("'remote work laptop': the remote work policy ranks first (most terms matched)",
            remote.Count > 0 && remote[0].Name == "remote-work-policy" && remote[0].TermsMatched == 3,
            Ranked(remote));

        IReadOnlyList<KbMatch> laptop = KeywordMatcher.Search(docs, "LAPTOP");
        Expect("matching ignores case; ties are ordered by name",
            string.Join(",", laptop.Select(m => m.Name)) == "onboarding-guide,remote-work-policy", Ranked(laptop));

        IReadOnlyList<KbMatch> common = KeywordMatcher.Search(docs, "Contoso", max: 10);
        Expect("at most 3 results by default; a word in every document fills them",
            KeywordMatcher.Search(docs, "Contoso").Count == 3 && common.Count == 5, $"'Contoso' matches {common.Count} of {docs.Count}");

        Expect("a query of short words only matches nothing", KeywordMatcher.Search(docs, "a of to").Count == 0);

        IReadOnlyList<KbMatch> vacation = KeywordMatcher.Search(docs, "vacation");
        Expect("LIMIT: 'vacation' finds nothing, though leave-and-pto is about it (keywords, not meaning)",
            vacation.Count == 0, "this is the gap the RAG chapter closes with embeddings");

        Console.WriteLine("\n-- Documents by name (resource kb://docs/{name}) --");
        Expect("kb://docs/remote-work-policy returns that document",
            KnowledgeBaseResources.GetDocument("remote-work-policy").StartsWith("# Contoso Remote Work Policy"));
        Expect("a path in the name ('../appsettings') finds nothing: names are matched, never joined into a path",
            KnowledgeBaseStore.Find("../appsettings") is null && KnowledgeBaseStore.Find("..\\docs\\code-of-conduct") is null);
        Expect("an unknown name is a protocol error, not an empty document",
            Throws<McpException>(() => KnowledgeBaseResources.GetDocument("salaries")));

        Console.WriteLine("\n-- Search tool --");
        string tool = KnowledgeBaseTools.SearchKnowledgeBase("expense receipts");
        Expect("the tool returns ranked excerpts with their match counts",
            tool.StartsWith("## expense-reimbursement (terms matched: 2)"), Line(tool));
        string original = KnowledgeBaseStore.DocsPath;
        KnowledgeBaseStore.DocsPath = Path.Combine(Path.GetTempPath(), "no-such-kb-" + Guid.NewGuid().ToString("N"));
        bool emptyIsError = Throws<McpException>(() => KnowledgeBaseTools.SearchKnowledgeBase("remote"));
        KnowledgeBaseStore.DocsPath = original;
        Expect("no docs folder: the tool reports an error (isError), not 'no match'", emptyIsError);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static string Ranked(IEnumerable<KbMatch> matches) =>
        string.Join(", ", matches.Select(m => $"{m.Name} ({m.TermsMatched})"));

    static string Line(string s) => s.Split('\n')[0];

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    static void Expect(string name, bool ok, string? detail = null)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        if (detail is not null) Console.WriteLine($"        {detail}");
    }
}
