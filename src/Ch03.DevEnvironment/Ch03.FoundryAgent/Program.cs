// ============================================================================
// Microsoft Foundry agent (PREVIEW, off the default path)
// ----------------------------------------------------------------------------
// Foundry returns an AIAgent, not an IChatClient, so it registers through
// AddFoundryAgent from AgenticAI.Common.Foundry (Microsoft.Agents.AI.Foundry
// and Azure.AI.Projects are prerelease packages). Cloud only: there is no
// offline model behind it.
//
//   dotnet run -- --config-check   names and sources of the settings, no call
//   dotnet run                     one RunAsync against your Foundry project
//
// Set LlmProvider:Foundry:ProjectEndpoint and :Model with user secrets or
// environment variables. DefaultAzureCredential signs you in (az login), but
// the identity still needs a role assignment on the Foundry project.
// ============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
try
{
    builder.Services.AddFoundryAgent(builder.Configuration,   // --config-check stops here
        instructions: "You are a friendly assistant. Keep your answers brief.",
        name: "HelloFoundry");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);
    return 1;
}

using IHost host = builder.Build();
AIAgent agent = host.Services.GetRequiredService<AIAgent>();

AgentResponse response = await agent.RunAsync("In one sentence, what is a Foundry project?");
Console.WriteLine(response.Text);
return 0;
