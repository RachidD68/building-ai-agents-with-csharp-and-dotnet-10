// ============================================================================
// Ch18.AguiServer: the Contoso assistant over AG-UI, after authorization.
// ----------------------------------------------------------------------------
//   POST /agui/assistant   AG-UI (MapAGUI), policy "employee"
//   POST /agui/review      AG-UI (MapAGUI), policy "finance-reviewer"
//   GET  /conversations/{thread}/events | snapshot   the operation's state (GA SSE)
//   GET  /reviews/{id}, POST /reviews/{id}/decision   the review screen
// The AG-UI thread id belongs to the first caller who uses it; the agent's
// tools read the caller from each request; the application, not the model,
// sets AwaitingApproval and Completed.
//
//   dotnet run                 Kestrel; tokens from: dotnet user-jwts create
//                              --name E-1001 --claim tid=contoso
//   dotnet run -- --self-test  offline: fake model, TestServer, minted tokens
// PREVIEW: Microsoft.Agents.AI.Hosting.AGUI.AspNetCore 1.5.0-preview (with
// Hosting 1.5.0-preview). GA alternative: Ch18.SseStatusStream. Simulated
// effects only: nothing is paid.
// ============================================================================

using Ch18.AguiServer;

if (args.Contains("--self-test")) return await AguiSelfTest.RunAsync();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
AguiHost.Configure(builder);
WebApplication app = builder.Build();
AguiHost.Map(app).Run();
return 0;
