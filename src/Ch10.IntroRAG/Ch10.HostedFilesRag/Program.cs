// ============================================================================
// Chapter 10 — Hosted RAG with Foundry (file search over a vector store)
// ----------------------------------------------------------------------------
// In the rest of Chapter 10 you built a RAG pipeline by hand: chunk documents,
// embed each chunk, store the embeddings in a vector index, retrieve the
// top-K matches at query time, stuff them into the prompt. That pattern is
// portable — every provider supports it — and it gives you full control.
//
// This program is the OTHER path: Foundry's hosted file-search tool, new in
// MAF 1.6. The mental model is NOT "upload files to a session". It is:
//
//   1. Provision a vector store in your Foundry project (portal or the
//      Azure.AI.Projects SDK) and upload your documents into it. Foundry
//      chunks, embeds, and indexes them for you.
//   2. In code, create a file-search AITool that points at that vector
//      store's id: FoundryAITool.CreateFileSearchTool([vectorStoreId]).
//   3. Attach the tool to a Foundry agent. Retrieval happens server-side
//      whenever the model decides it needs it — no chunk/embed/query code
//      on your side.
//
// Trade-offs:
//   - hosted: faster to ship, tied to Foundry, retrieval is server-side
//   - custom (the rest of Ch10): portable across providers, more code
//
// To run: configure LlmProvider:Foundry:ProjectEndpoint AND a vector-store id
// in LlmProvider:Foundry:VectorStoreId, and `az login` so DefaultAzure-
// Credential can authenticate. If either is missing the program prints setup
// guidance and exits cleanly.
// ============================================================================

using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Configuration;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
IConfiguration config = builder.Configuration;

LlmSettings settings = new();
config.GetSection("LlmProvider").Bind(settings);

string? vectorStoreId = config["LlmProvider:Foundry:VectorStoreId"];

if (string.IsNullOrWhiteSpace(settings.Foundry.ProjectEndpoint) ||
    string.IsNullOrWhiteSpace(vectorStoreId))
{
    Console.WriteLine("Hosted RAG requires Microsoft Foundry — see Chapter 4.");
    Console.WriteLine("1. Configure LlmProvider:Foundry:ProjectEndpoint in appsettings.json.");
    Console.WriteLine("2. In your Foundry project, create a vector store and upload the");
    Console.WriteLine("   files in this project's data/ folder into it.");
    Console.WriteLine("3. Put the vector store id in LlmProvider:Foundry:VectorStoreId.");
    Console.WriteLine("4. Run `az login` so DefaultAzureCredential can authenticate.");
    return;
}

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 10: Hosted RAG with Foundry ===");
Console.WriteLine($"Project endpoint: {settings.Foundry.ProjectEndpoint}");
Console.WriteLine($"Vector store:     {vectorStoreId}");
Console.WriteLine();

// ── Create the file-search tool over the project's vector store ────
// FoundryAITool.CreateFileSearchTool returns a Microsoft.Extensions.AI.AITool
// that, when the model invokes it, searches the named vector store(s)
// server-side and returns grounded passages. No chunker/embedder here.
//
// FoundryAITool was [Experimental("OPENAI001")] through MAF 1.6; as of 1.7+
// the experimental flag was removed (the Foundry "responses" surface stabilised),
// so no suppression is needed in 1.8.0.
AITool fileSearch = FoundryAITool.CreateFileSearchTool(
    vectorStoreIds: [vectorStoreId]);

// ── Build the Foundry agent with the file-search tool attached ─────
AIAgent agent = new AIProjectClient(
        new Uri(settings.Foundry.ProjectEndpoint),
        new DefaultAzureCredential())
    .AsAIAgent(
        model: settings.Foundry.Model,
        instructions: """
            You are the Contoso HR assistant. Answer questions about Contoso's
            policies using ONLY the information returned by the file-search
            tool. If the tool returns nothing relevant, say you don't know.
            Cite the section heading when you can.
            """,
        name: "ContosoHostedFaq",
        description: "HR assistant grounded in Contoso policy documents.",
        tools: [fileSearch]);

AgentSession session = await agent.CreateSessionAsync(cts.Token);

// ── Ask grounded questions ─────────────────────────────────────────
string[] questions =
[
    "How many PTO days do new Contoso employees get?",
    "How does parental leave differ for birth vs. non-birth parents?",
    "What is the 401(k) match percentage?",
    "Does Contoso offer tuition reimbursement, and if so, what is the annual cap?",
];

foreach (string q in questions)
{
    Console.WriteLine($"You: {q}");
    Console.Write("Agent: ");
    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
        q, session, cancellationToken: cts.Token))
    {
        if (update.Text is { Length: > 0 } text)
        {
            Console.Write(text);
        }
    }
    Console.WriteLine();
    Console.WriteLine();
}

Console.WriteLine("=== Done ===");
