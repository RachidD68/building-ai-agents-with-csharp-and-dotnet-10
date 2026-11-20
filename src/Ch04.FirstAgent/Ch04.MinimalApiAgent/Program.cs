// ============================================================================
// Contoso expense help over HTTP
// ----------------------------------------------------------------------------
// The policy agent behind two single-turn endpoints:
//
//   POST /chat          { "message": "..." } -> JSON with an explicit status
//   GET  /chat/stream   ?message=...         -> server-sent events, last event 'done'
//
// Single-turn: every request runs with no session, so no request sees another's
// history. A message over the size budget is a 400 validation error with no
// model call; a provider error is a 502 and a run over the time budget a 504.
//
//   dotnet run                    against the configured model (Ollama llama3.2)
//   dotnet run -- --self-test     the real app on a loopback port with a fake
//                                 model, no outside network; RESULT n/n
// ============================================================================

using Ch04.MinimalApiAgent;

if (args.Contains("--self-test")) return await ApiSelfTest.RunAsync();

WebApplication app = AgentApi.Build(args);
app.Run();
return 0;
