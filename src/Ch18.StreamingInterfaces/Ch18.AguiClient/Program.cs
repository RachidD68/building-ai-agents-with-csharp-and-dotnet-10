// ============================================================================
// Ch18.AguiClient: a C# client for the AG-UI host.
// ----------------------------------------------------------------------------
// The response comes over AG-UI (POST /agui/assistant, SSE events); the
// operation's state comes over the status stream (/conversations/{thread}).
// The client applies AG-UI events by run and message id, discards a cut run
// and resyncs from the status snapshot, and prints DONE only for a terminal
// state of the operation, never for RUN_FINISHED.
//
//   dotnet run                 the live run: Ch18.AguiServer in process with
//                              the LlmProvider model (Ollama by default),
//                              minted tokens; then MAF's AGUIChatClient
//   dotnet run -- --self-test  offline: fake model (RESULT n/n)
// PREVIEW: Microsoft.Agents.AI.AGUI 1.13.0-preview (no 1.28 build on NuGet)
// against AG-UI hosting 1.28.0-preview. Simulated effects only.
// ============================================================================

using System.Net.Http.Json;
using System.Text.Json;
using AgenticAI.Common.Extensions;
using Ch18.AguiClient;
using Ch18.AguiServer;
using Ch18.ApprovalUi;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

if (args.Contains("--self-test")) return await AguiClientSelfTest.RunAsync();

Console.WriteLine("=== Ch18.AguiClient: live run (Ch18.AguiServer in process, LlmProvider model) ===\n");
HostApplicationBuilder modelHost = Host.CreateApplicationBuilder(args);   // appsettings, user secrets, environment
modelHost.Services.AddLlmChatClient(modelHost.Configuration);
using IHost models = modelHost.Build();
await using TestHost host = await TestHost.StartAsync(models.Services.GetRequiredService<IChatClient>());
string alice = host.Token("contoso", "E-1001"), rita = host.Token("contoso", "rita");
ContosoClient ui = new(host.Http, alice);

Console.WriteLine("-- alice, over AG-UI: \"Please submit my taxi receipt of 120 EUR.\"");
AguiRunView run = await ui.RunAsync("/agui/assistant", "t-live", "Please submit my taxi receipt of 120 EUR.", onEvent: e =>
{
    string type = e.GetProperty("type").GetString()!;
    if (type != "TEXT_MESSAGE_CONTENT") Console.WriteLine($"   {type}{(e.TryGetProperty("toolCallName", out JsonElement n) ? " " + n.GetString() : "")}");
});
Console.WriteLine($"   text: \"{run.Text.Trim()}\"");
Console.WriteLine($"   tools: {string.Join(", ", run.Tools.Values)}");
await ui.ResyncAsync("t-live");
Console.WriteLine($"   {ui.Screen(run)}");

if (ui.Status.State == Ch18.SseStatusStream.OperationState.AwaitingApproval)
{
    Console.WriteLine("\n-- rita opens the review screen and approves");
    Guid id = host.Claims.Pending().First();   // the reviewer's queue
    HttpRequestMessage get = new(HttpMethod.Get, $"/reviews/{id}");
    get.Headers.Authorization = new("Bearer", rita);
    ReviewView view = (await (await host.Http.SendAsync(get)).Content.ReadFromJsonAsync<ReviewView>())!;
    Console.WriteLine($"   review: {view.Amount} {view.Currency} for {view.Requester}, version {view.Version}");
    HttpRequestMessage post = new(HttpMethod.Post, $"/reviews/{id}/decision")
    {
        Content = JsonContent.Create(new DecisionRequest(view.Version, view.Digest, "approve")),
    };
    post.Headers.Authorization = new("Bearer", rita);
    Console.WriteLine($"   decision: {(int)(await host.Http.SendAsync(post)).StatusCode}");
    await ui.FollowAsync("t-live", TimeSpan.FromSeconds(10), e => Console.WriteLine($"   status #{e.Cursor}: {e.Type} {e.State} {e.Reason}"));
    Console.WriteLine($"   {ui.Screen(run)}");
}

Console.WriteLine("\n-- the same host through MAF's AGUIChatClient (Microsoft.Agents.AI.AGUI)");
HttpClient bearer = host.Http;
bearer.DefaultRequestHeaders.Authorization = new("Bearer", alice);
AIAgent remote = new AGUIChatClient(bearer, "/agui/assistant").AsAIAgent(name: "contoso-remote", instructions: null);
AgentSession session = await remote.CreateSessionAsync();
System.Text.StringBuilder text = new();
int updates = 0;
await foreach (AgentResponseUpdate u in remote.RunStreamingAsync("List my claims.", session))
{
    updates++;
    text.Append(u.Text);
}
Console.WriteLine($"   {updates} updates: \"{text.ToString().Trim()}\"");
return 0;
