// ============================================================================
// Sequential pipeline: chain agents by hand, or with BuildSequential
// ----------------------------------------------------------------------------
//   dotnet run                Translator -> Summarizer, twice: a manual chain
//                             (each agent sees only the previous output) and
//                             AgentWorkflowBuilder.BuildSequential (each agent
//                             sees the whole conversation so far)
//   dotnet run -- --content   Researcher -> Writer -> Editor with BuildSequential;
//                             the researcher looks up the Contoso travel policy
//                             with a local tool, and the editor checks the draft
//                             against the research because it sees it
//
// Absorbs the first edition's ContentPipeline. The offline proof (same final
// text either way; what the second agent is shown) is in Ch13.MultiAgent.Tests.
// ============================================================================

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (args.Contains("--content"))
{
    await ContentPipelineAsync();
    return;
}

Console.WriteLine("=== Sequential pipeline: Translator -> Summarizer ===\n");

ChatClientAgent translator = new(chatClient,
    instructions: "Translate the French text into fluent English. Output only the translation.",
    name: "Translator");
ChatClientAgent summarizer = new(chatClient,
    instructions: "Summarize the English text in two sentences. Output only the summary.",
    name: "Summarizer");

const string frenchMemo = """
    Objet : notes de frais de novembre. À partir du 1er décembre, chaque note de
    frais doit être accompagnée d'un justificatif dont le montant correspond
    exactement au montant déclaré. Les nuits d'hôtel sont plafonnées à 150 EUR et
    les repas à 60 EUR par note. Les notes incomplètes seront renvoyées à leur auteur.
    """;
Console.WriteLine($"[Input]\n{frenchMemo.Trim()}\n");

// ── A. Manual chain: you pass exactly what the next stage may see ───────
AgentResponse english = await translator.RunAsync(frenchMemo, await translator.CreateSessionAsync(cts.Token),
    cancellationToken: cts.Token);
AgentResponse manualSummary = await summarizer.RunAsync(english.Text, await summarizer.CreateSessionAsync(cts.Token),
    cancellationToken: cts.Token);
Console.WriteLine("--- A. Manual chain (the summarizer sees the translation only) ---");
Console.WriteLine($"[Translator] {english.Text.Trim()}");
Console.WriteLine($"[Summarizer] {manualSummary.Text.Trim()}\n");

// ── B. BuildSequential: the framework passes the conversation along ─────
Workflow workflow = AgentWorkflowBuilder.BuildSequential(translator, summarizer);
Console.WriteLine("--- B. BuildSequential (the summarizer sees the memo and the translation) ---");
foreach (ChatMessage message in await RunAsync(workflow, frenchMemo))
    Console.WriteLine($"[{message.AuthorName ?? message.Role.ToString()}] {message.Text.Trim()}");

Console.WriteLine("\n=== Done ===");

// ── The content pipeline: three stages, the editor sees the research ────
async Task ContentPipelineAsync()
{
    Console.WriteLine("=== Content pipeline: Researcher -> Writer -> Editor ===\n");

    ChatClientAgent researcher = new(chatClient,
        instructions: """
            You research a question for an employee FAQ. Call the policy lookup tool for each
            topic you need, then list the facts you found as short bullets, each with its topic.
            Output only the bullets.
            """,
        name: "Researcher",
        tools: [AIFunctionFactory.Create(LookUpPolicy)]);
    ChatClientAgent writer = new(chatClient,
        instructions: """
            You write a short FAQ answer (at most 120 words) for Contoso employees, using only
            the researcher's bullets. Output only the answer.
            """,
        name: "Writer");
    ChatClientAgent editor = new(chatClient,
        instructions: """
            You edit the writer's FAQ answer. Check every number against the researcher's bullets
            earlier in this conversation. Output two sections: "Corrections" (or "None") and
            "Final answer".
            """,
        name: "Editor");

    const string question = "What can I claim for a hotel stay and a dinner on a business trip?";
    Console.WriteLine($"[Question] {question}\n");

    Workflow content = AgentWorkflowBuilder.BuildSequential(researcher, writer, editor);
    foreach (ChatMessage message in await RunAsync(content, question))
    {
        if (message.Role != ChatRole.Assistant || message.Text.Length == 0) continue;
        Console.WriteLine($"=== {message.AuthorName} ===\n{message.Text.Trim()}\n");
    }
    Console.WriteLine("=== Done ===");
}

async Task<List<ChatMessage>> RunAsync(Workflow pipeline, string input)
{
    await using Run run = await InProcessExecution.Default.RunAsync(pipeline, input, cancellationToken: cts.Token);
    return run.OutgoingEvents.OfType<WorkflowOutputEvent>()
        .Select(e => e.Data).OfType<List<ChatMessage>>().LastOrDefault() ?? [];
}

// A local lookup over the Contoso travel policy used across this chapter.
[Description("Looks up one topic of the Contoso travel policy: hotel, meal, receipt, taxi, train or parking.")]
static string LookUpPolicy([Description("The policy topic")] string topic) =>
    topic.Trim().ToLowerInvariant() switch
    {
        "hotel" => "hotel: at most 150 EUR per night (the claimed amount divided by the nights).",
        "meal" or "dinner" or "lunch" => "meal: at most 60 EUR per claim.",
        "receipt" => "receipt: required for every claim; its amount must equal the claimed amount.",
        "taxi" or "train" or "parking" => $"{topic}: no cap; a receipt is still required.",
        _ => $"no policy entry for '{topic}'. Known topics: hotel, meal, receipt, taxi, train, parking.",
    };
