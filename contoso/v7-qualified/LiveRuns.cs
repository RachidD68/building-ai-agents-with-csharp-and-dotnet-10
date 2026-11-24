using System.Globalization;
using AgenticAI.Common.Extensions;
using Ch08.ActionPolicy;
using Ch10.FaqAgent;
using Ch21.EvidenceEval;
using CommunityToolkit.VectorData.InMemory;
using Contoso.AuthorizedReimbursement;
using Contoso.FaqEvidence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Contoso.Qualified;

/// <summary>
/// The live runs the real-provider layer starts. Both run the configuration contoso-v7.2 ships,
/// not a teaching sample: --evidence-live scores the requester assistant (tools by role, answer
/// contract) on the evaluation chapter's cases with its five layers; --faq-live runs v3's two
/// FAQ desks, unchanged, on v3's live scenarios, with the answer contract around the model.
/// </summary>
internal static class LiveRuns
{
    private static (IHost Host, IChatClient Model, IConfiguration Config) Model(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddLlmChatClient(builder.Configuration);
        IHost host = builder.Build();
        return (host, host.Services.GetRequiredService<IChatClient>(), host.Services.GetRequiredService<IConfiguration>());
    }

    public static async Task<int> EvidenceAsync(string[] args)
    {
        (IHost host, IChatClient model, IConfiguration config) = Model(args);
        using IHost _ = host;
        int repsAt = Array.IndexOf(args, "--reps");
        int reps = repsAt >= 0 && repsAt + 1 < args.Length ? int.Parse(args[repsAt + 1], CultureInfo.InvariantCulture) : 3;
        CaseBook book = CaseBook.Load(Path.Combine(AppContext.BaseDirectory, "cases", "evidence-cases.v1.json"));

        // The shipped profile and a requester: Alice, who may submit claims and decide none.
        ReleaseProfile release = ReleaseProfile.Load(ReleaseProfile.DefaultFile, config["Release:Profile"] ?? "release");
        PermissionDirectory directory = new();
        directory.Grant("contoso", "E-1001", Permissions.SubmitReimbursement);
        Caller alice = directory.Authenticate("contoso", "E-1001");
        ClaimActions actions = new(directory, release, new Dictionary<string, string> { ["C-1001"] = "E-1001", ["C-1002"] = "E-1001" });
        AnswerContractClient contract = RequesterAssistant.Contract(model);
        ChatClientAgent agent = RequesterAssistant.For(contract, alice, actions);

        Console.WriteLine("=== Evidence evaluation (live): the Contoso v7 requester assistant ===");
        Console.WriteLine($"model {config["LlmProvider:Ollama:Model"] ?? "llama3.2"}; cases {book.Version} ({book.Cases.Count}); numRepetitions {reps}");
        Console.WriteLine($"caller contoso/E-1001 (requester), profile {release.Profile}; tools offered: " +
                          string.Join(", ", RequesterAssistant.ToolsFor(alice, actions).Select(t => t.Name)));
        Console.WriteLine();

        AgentEvaluationResults results;
        try
        {
            results = await agent.EvaluateAsync(book.Cases.Select(c => c.Prompt()), EvidenceChecks.Evaluator(book),
                evalName: book.Version, numRepetitions: reps);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"The model is not reachable: {ex.Message}");
            return 2;
        }

        // The same report as the evaluation chapter's live run, so the matrix reads the same lines.
        IReadOnlyList<EvalItem> inputs = results.InputItems ?? [];
        Console.WriteLine($"{"case",-17} {"fact",-5} {"supp",-5} {"cite",-5} {"abst",-5} {"act",-5}  answer");
        for (int i = 0; i < results.Items.Count; i++)
        {
            EvidenceCase c = book.For(inputs[i].Query);
            string Cell(string name) => results.Items[i].Metrics.TryGetValue(name, out EvaluationMetric? m)
                ? m.Reason?.StartsWith("n/a", StringComparison.Ordinal) == true ? "-" : m.Interpretation?.Failed == true ? "FAIL" : "pass"
                : "?";
            string answer = inputs[i].Response.ReplaceLineEndings(" ");
            Console.WriteLine($"{c.Id,-17} {Cell("fact.value"),-5} {Cell("evidence.support"),-5} {Cell("citation.issued"),-5} " +
                              $"{Cell("abstention"),-5} {Cell("action.allowed"),-5}  {(answer.Length > 70 ? answer[..70] + "..." : answer)}");
        }

        Console.WriteLine();
        Console.WriteLine("Action layer, per item:");
        for (int i = 0; i < results.Items.Count; i++)
        {
            if (!results.Items[i].Metrics.TryGetValue("action.allowed", out EvaluationMetric? m)
                || m.Reason?.StartsWith("n/a", StringComparison.Ordinal) != false) continue;
            string[] calls = [.. EvidenceChecks.ToolCalls(inputs[i]).Select(f => $"{f.Name}({string.Join(", ", f.Arguments?.Values ?? [])})")];
            Console.WriteLine($"  {book.For(inputs[i].Query).Id,-17} {(m.Interpretation?.Failed == true ? "FAIL" : "pass")}  {m.Reason}; " +
                              $"calls: {(calls.Length == 0 ? "none" : string.Join(", ", calls))}");
        }

        Console.WriteLine();
        Console.WriteLine("Per layer (applicable items only):");
        foreach (string name in EvidenceChecks.Names)
        {
            EvaluationMetric[] applicable = [.. results.Items
                .Select(r => r.Metrics.TryGetValue(name, out EvaluationMetric? m) ? m : null)
                .OfType<EvaluationMetric>()
                .Where(m => m.Reason?.StartsWith("n/a", StringComparison.Ordinal) != true)];
            Console.WriteLine($"  {name,-17} {applicable.Count(m => m.Interpretation?.Failed != true)}/{applicable.Length}");
        }
        Console.WriteLine($"Items: {inputs.Count} evaluated ({book.Cases.Count} cases x {reps} repetitions), " +
                          $"Passed {results.Passed}/{results.Total}");
        int approveCalls = inputs.Sum(item => EvidenceChecks.ToolCalls(item).Count(f => f.Name == "approve_reimbursement"));
        Console.WriteLine($"approve_reimbursement: not offered to this caller; calls to it: {approveCalls}; effects: " +
                          (actions.Effects.Count == 0 ? "none" : string.Join("; ", actions.Effects.Distinct())));
        PrintContract(contract);
        Console.WriteLine();
        Console.WriteLine("One small local model, one run: these counts describe this run only.");
        return results.AllPassed ? 0 : 1;
    }

    public static async Task<int> FaqAsync(string[] args)
    {
        (IHost host, IChatClient model, IConfiguration config) = Model(args);
        using IHost _ = host;
        Console.WriteLine("=== Contoso: FAQ with evidence (v3's desks, behind v7's answer contract) ===");
        using IEmbeddingGenerator<string, Embedding<float>> embeddings = new OllamaSharp.OllamaApiClient(
            new Uri(config["LlmProvider:Ollama:Endpoint"] ?? "http://localhost:11434"),
            config["LlmProvider:Ollama:EmbeddingModel"] ?? "nomic-embed-text");
        await FaqIngest.CheckModelAsync(embeddings);
        IReadOnlyList<FaqChunk> chunks = await FaqIngest.LoadAsync(Path.Combine(AppContext.BaseDirectory, "docs"));
        InMemoryCollection<string, FaqChunk> collection = new("handbook");
        await FaqIngest.IngestAsync(embeddings, collection, chunks);
        Console.WriteLine($"Handbook indexed: {chunks.Count} chunks.");

        AnswerContractClient handbook = new(model, EvidenceFormat.Handbook, [FaqDesk.Abstention], FaqDesk.Abstention);
        FaqDesk desk = new(handbook, new FaqSearchService(embeddings, collection, topK: 3, scoreThreshold: 0.55),
            Scenario.Directory(), Scenario.Tenant);
        await Scenario.RunAsync(desk, assert: false);
        PrintContract(handbook);

        AnswerContractClient policies = new(model, EvidenceFormat.Versioned, ["do not know", "don't know"], "I do not know.");
        int code = await PermissionsScenario.LiveAsync(policies, config);
        PrintContract(policies);
        return code;
    }

    private static void PrintContract(AnswerContractClient contract)
    {
        Console.WriteLine($"Answer contract: {contract.Log.Count} answers; " + string.Join(", ",
            contract.Log.GroupBy(r => r.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
        foreach (ContractRecord r in contract.Log.Where(r => r.ModelCalls > 1))
            Console.WriteLine($"  retried: {r.Outcome} ({r.Reason})");
    }
}
