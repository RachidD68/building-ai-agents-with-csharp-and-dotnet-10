// ============================================================================
// A coding agent scoped to a scratch repo
// ----------------------------------------------------------------------------
// The agent gets three tools over one folder: list_files, read_file and
// write_file. ScratchScope resolves every path (relative only, inside the
// root, never under .git, never through a link), and write_file is an
// ApprovalRequiredAIFunction, so each write comes back to you as a
// ToolApprovalRequestContent before anything touches the disk.
//
// The chat client is the configured provider (Ollama by default). The
// chapter's GitHub Copilot provider (Microsoft.Agents.AI.GitHub.Copilot) is
// not in this repo yet: the package could not be restored offline here, so
// nothing in this project calls it. See README.md.
//
//   dotnet run                  live run in a fresh scratch copy under %TEMP%
//   dotnet run -- --self-test   offline checks, no model and no Copilot call
// ============================================================================

using AgenticAI.Common.Extensions;
using Ch16.CodingAgent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test")) return await SelfTest.RunAsync();

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Services.AddLlmChatClient(hostBuilder.Configuration, attemptTimeout: TimeSpan.FromMinutes(8));
using IHost host = hostBuilder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Coding agent (scratch repo) ===");
Console.WriteLine();

// A throwaway copy, so a bad write costs nothing.
ScratchScope scope = new(Path.Combine(Path.GetTempPath(), "ch16-scratch-" + Guid.NewGuid().ToString("N")[..8]));
SampleRepo.Create(scope.Root);
Console.WriteLine($"Scratch repo: <temp>/{Path.GetFileName(scope.Root)}");   // no user path in the transcript
Console.WriteLine();

ChatClientAgent agent = CodingTools.CreateAgent(chatClient, scope);
AgentSession session = await agent.CreateSessionAsync(cts.Token);

const string task =
    "In src/Discount.cs, make Apply throw an ArgumentOutOfRangeException when percent is " +
    "below 0 or above 100. Read the file first, then write the whole file back.";
Console.WriteLine($"You: {task}");

AgentResponse response = await agent.RunAsync(task, session, cancellationToken: cts.Token);
for (int round = 0; round < 3; round++)
{
    List<ToolApprovalRequestContent> requests =
        [.. response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>()];
    if (requests.Count == 0) break;

    List<AIContent> decisions = [];
    foreach (ToolApprovalRequestContent request in requests)
    {
        FunctionCallContent call = (FunctionCallContent)request.ToolCall;
        Console.WriteLine();
        Console.WriteLine($"[approval] {call.Name} {Arg(call, "path")}:");
        Console.WriteLine(Arg(call, "content"));
        Console.Write("Approve this write? [y/N] ");
        // No console (redirected input that has ended) reads null: that is a no.
        bool approved = Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
        Console.WriteLine(approved ? "approved" : "rejected");
        decisions.Add(request.CreateResponse(approved, approved ? "Reviewed." : "Not approved."));
    }
    response = await agent.RunAsync([new ChatMessage(ChatRole.User, decisions)], session, cancellationToken: cts.Token);
}

Console.WriteLine();
Console.WriteLine($"Agent: {response.Text.Trim()}");
Console.WriteLine();
Console.WriteLine("src/Discount.cs now:");
Console.WriteLine(File.ReadAllText(Path.Combine(scope.Root, "src", "Discount.cs")));
Console.WriteLine("=== Coding agent demo complete! ===");
return 0;

static string Arg(FunctionCallContent call, string name) =>
    call.Arguments is { } a && a.TryGetValue(name, out object? v) ? v?.ToString() ?? "" : "";
