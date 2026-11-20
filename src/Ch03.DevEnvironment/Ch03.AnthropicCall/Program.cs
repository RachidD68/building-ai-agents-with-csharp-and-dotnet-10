// ============================================================================
// Anthropic (Claude) through AgenticAI.Common.Anthropic
// ----------------------------------------------------------------------------
// Like Foundry, Anthropic returns an AIAgent, not an IChatClient, so it has its
// own registration: AddAnthropicAgent (Microsoft.Agents.AI.Anthropic, GA). The
// key comes from LlmProvider:Anthropic:ApiKey or, as a fallback, the
// conventional ANTHROPIC_API_KEY environment variable.
//
//   dotnet run -- --config-check   names and sources of the settings, no call
//   dotnet run                     one RunAsync, billed to your Anthropic account
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
try
{
    builder.Services.AddAnthropicAgent(builder.Configuration, // --config-check stops here
        instructions: "You are a concise assistant for .NET developers.",
        name: "FirstClaude");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);   // names the missing setting, never a value
    return 1;
}

using IHost host = builder.Build();
AIAgent agent = host.Services.GetRequiredService<AIAgent>();

AgentResponse response = await agent.RunAsync("In one sentence, what does dotnet user-secrets store?");
Console.WriteLine(response.Text);
return 0;
