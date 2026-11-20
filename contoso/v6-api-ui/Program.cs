// ============================================================================
// Contoso v6: the API half (the hosting chapter) and the interface half
// (the interface chapter), in one snapshot.
// ----------------------------------------------------------------------------
// v5 runs durably on one machine; v6 puts it behind an ASP.NET Core API that
// employees of two organisations reach over HTTP. Every request carries a
// validated token; its (tid, sub) is the identity key.
//
//   from Ch17.AspNetCoreHost (its composition, used as it is):
//     JWT bearer, POST /ask (single-turn), POST /conversations and
//     /conversations/{id}/messages (owned, versioned), the (tenant, subject)
//     rate limit, one retry owner, /health/live and /health/ready
//   new in v6, over v5's store:
//     GET  /claims/{id}            the requester or a finance reviewer; else 404
//     POST /claims/{id}/decision   {"decision":"approve"|"reject"}; the reviewer
//                                  is the caller, checked against v5's rules
//   new in v6, the interface half (InterfaceApi):
//     GET  /claims/{id}/events     the claim's operation state, typed SSE,
//                                  resumable with Last-Event-ID
//     GET  /claims/{id}/snapshot   the same view at the current cursor
//     GET  /claims/{id}/review     amount, currency, account version, digest
//     POST /claims/{id}/approval   {"accountVersion":7,"digest":"...",
//                                  "decision":"approve"|"reject"}
//   still v5's worker, a separate process on the same store:
//     dotnet run --project contoso/v5-durable-resume -- prepare | resume <id>
//
//   dotnet run                    Ollama by default; store: Contoso:Store
//                                 (default: v5's default store folder)
//   dotnet run -- --self-test     v5 prepare and resume as child processes, the
//                                 API on a TestServer between them, offline
// Simulated effects only; Microsoft.Agents.AI.Hosting is a preview package.
// ============================================================================

using Ch17.AspNetCoreHost;
using Contoso.ApiUi;

if (args.Contains("--self-test")) return await V6SelfTest.RunAsync();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string store = builder.Configuration["Contoso:Store"] ?? ClaimsApi.DefaultStore;
builder.Configuration["Conversations:DatabasePath"] ??= Path.Combine(store, "conversations.db");
AgentHost.Configure(builder).Services.AddSingleton<ClaimFeed>().AddHostedService(sp => sp.GetRequiredService<ClaimFeed>());
WebApplication app = builder.Build();
AgentHost.Map(app);
InterfaceApi.Map(ClaimsApi.Map(app, store), store).Run();
return 0;
