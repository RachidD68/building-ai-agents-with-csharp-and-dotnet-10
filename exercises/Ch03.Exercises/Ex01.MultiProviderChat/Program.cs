// ============================================================================
// Exercise 01 — Multi-provider chat (Basic)
// ----------------------------------------------------------------------------
// The provider comes from configuration only (LlmProvider:Provider): switch
// from ollama to openai or azure with a user secret or an environment variable,
// never with a code change. FULLY STANDALONE: no reference to AgenticAI.Common.
//
//   dotnet run -- --self-test      offline: the switch, with a fake client, RESULT n/n
//   dotnet run -- --config-check   the active provider and where it came from
//   dotnet run                     chat; an empty line or "quit" ends it
// ============================================================================

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Ex01.MultiProviderChat;

if (args.Contains("--self-test"))
{
    return SelfTest.Run();
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
Console.WriteLine(ProviderSwitch.Describe(builder.Configuration));
if (args.Contains("--config-check"))
{
    return 0;
}

IChatClient chat;
try
{
    chat = ProviderSwitch.Create(builder.Configuration);
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);
    return 1;
}

Console.WriteLine("Type a message (an empty line or quit to exit).\n");
while (Console.ReadLine() is { Length: > 0 } input && !input.Equals("quit", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(await ChatTurn.AskAsync(chat, input) + "\n");
}
return 0;
