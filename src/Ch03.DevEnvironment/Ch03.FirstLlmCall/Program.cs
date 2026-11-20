// ============================================================================
// Your first LLM call
// ----------------------------------------------------------------------------
// One IChatClient from the LlmProvider configuration section, one prompt, one
// answer. Three ways to run it, from cheapest to the one that calls a model:
//
//   dotnet run -- --self-test      offline: the configuration rules, RESULT n/n
//   dotnet run -- --config-check   names and sources of the settings, no call
//   dotnet run                     one GetResponseAsync on the configured provider
//
// The default provider is a local Ollama (llama3.2). Secrets go in user secrets
// or environment variables, never in appsettings.json.
// ============================================================================

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Configuration;
using AgenticAI.Common.Extensions;
using Ch03.FirstLlmCall;

if (args.Contains("--self-test"))
{
    return ConfigSelfTest.Run();
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAzureOpenAIProvider();                // the azure arm (prerelease package)
builder.Services.AddLlmChatClient(builder.Configuration); // --config-check stops here

using IHost host = builder.Build();
IChatClient chat = host.Services.GetRequiredService<IChatClient>();

LlmSettings settings = new();
builder.Configuration.GetSection("LlmProvider").Bind(settings);
string prompt = "In one sentence, what does a global.json file do in a .NET repository?";
Console.WriteLine($"Provider: {settings.Provider}");
Console.WriteLine($"Prompt:   {prompt}\n");

try
{
    ChatResponse response = await chat.GetResponseAsync(prompt);
    Console.WriteLine(response.Text);
    if (response.Usage is { } usage)
    {
        Console.WriteLine($"\nTokens: {usage.InputTokenCount ?? 0} in, {usage.OutputTokenCount ?? 0} out");
    }
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // The usual causes: Ollama not running, the model not pulled, a wrong key.
    Console.WriteLine($"The provider did not answer ({ex.GetType().Name}): {ex.Message}");
    Console.WriteLine("Run again with --config-check to see which settings were found.");
    return 1;
}
