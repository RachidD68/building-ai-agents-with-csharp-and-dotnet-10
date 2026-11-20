// ============================================================================
// The first agent's first test
// ----------------------------------------------------------------------------
// The Contoso policy agent's contracts, checked without a model: a fake
// IChatClient records what the agent sends, honours the token and scripts
// failures. It proves wiring and contracts, never the quality of an answer.
// Then --live checks the same contracts against the configured model.
//
//   dotnet run -- --self-test   a FakeChatClient, no network; RESULT n/n
//   dotnet run -- --live        the configured model (Ollama llama3.2 by default)
//
// A console harness like the repo's other tests: each check prints PASS or
// FAIL, and the exit code is non-zero when anything failed.
// ============================================================================

using Ch04.AgentTests;

if (args.Contains("--live")) return await LiveChecks.RunAsync(args);
if (args.Contains("--self-test") || args.Length == 0) return await OfflineChecks.RunAsync();
Console.Error.WriteLine("usage: dotnet run -- [--self-test | --live]");
return 2;
