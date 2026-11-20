// ============================================================================
// Contoso v7: the qualified version. v6 as it is, frozen as a candidate.
// ----------------------------------------------------------------------------
// v6 serves the claims API and the interface over v5's durable store. v7 adds
// what a team that inherits it needs to know what it is running:
//
//   release/candidate.json      the candidate manifest the qualification
//                               runner reads: projects, prompts, skills, data,
//                               model profile, layers, acceptance matrix, limits
//   release/capabilities.json   what this release ships, and the reason for
//                               every capability it ships disabled
//   release/failure-journal.json  each finding of the campaign and its disposition
//   GET /release                candidate, profile and capability states
//   the gate                    a disabled capability's routes: 503
//                               capability_disabled with the recorded reason
//   the route check             a mapped route in no capability stops the start
//
//   dotnet run                    the release profile (Release:Profile=release)
//   dotnet run -- --self-test     the gate, the refusals and the drill, offline
//   dotnet run -- --drill         the handoff drill alone, with its business state
// Simulated effects only; Microsoft.Agents.AI.Hosting is a preview package.
// ============================================================================

using Ch17.AspNetCoreHost;
using Contoso.ApiUi;
using Contoso.Qualified;

if (args.Contains("--self-test")) return await V7SelfTest.RunAsync(drillOnly: false);
if (args.Contains("--drill")) return await V7SelfTest.RunAsync(drillOnly: true);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string store = builder.Configuration["Contoso:Store"] ?? ClaimsApi.DefaultStore;
builder.Configuration["Conversations:DatabasePath"] ??= Path.Combine(store, "conversations.db");
ReleaseProfile release = ReleaseProfile.Load(ReleaseProfile.DefaultFile, builder.Configuration["Release:Profile"] ?? "release");
AgentHost.Configure(builder).Services.AddSingleton<ClaimFeed>().AddHostedService(sp => sp.GetRequiredService<ClaimFeed>());
WebApplication app = builder.Build();
AgentHost.Map(app);
release.Map(InterfaceApi.Map(ClaimsApi.Map(app, store), store)).Run();
return 0;
