// ============================================================================
// Ch17.AspNetCoreHost: one agent behind an ASP.NET Core API where every
// request carries a validated identity.
//
//   POST   /ask                                SINGLE-TURN: a fresh session, nothing stored
//   POST   /conversations                      CONVERSATIONAL: issue an id owned by the caller
//   POST   /conversations/{id}/messages        load authorized -> run -> save versioned
//   DELETE /conversations/{id}                 owner only; repeating it is a no-op
//   GET    /agents, /health/live, /health/ready   anonymous; neither health probe calls the model
//
// The identity key is (tenant, subject) from the validated token ("tid", "sub",
// MapInboundClaims = false). It feeds authorization (the route needs it), the
// rate-limit partition and the conversation store's partitions.
//
// Local tokens, no identity provider:
//   dotnet user-jwts create --claim tid=contoso --name alice
//   curl -H "Authorization: Bearer <token>" -X POST http://localhost:56400/conversations
//
//   dotnet run -- --self-test     identity key, partitions, retry owner (offline)
// The two-user proof over HTTP is Ch17.IdentityTests.
// ============================================================================

using Ch17.AspNetCoreHost;

if (args.Contains("--self-test")) return await HostSelfTest.RunAsync();

WebApplication app = AgentHost.Configure(WebApplication.CreateBuilder(args)).Build();
AgentHost.Map(app).Run();
return 0;
