// ============================================================================
// Chapter 8 — Controlling tool choice (ChatOptions.ToolMode)
// ----------------------------------------------------------------------------
// By default the model decides whether to call a tool. ChatOptions.ToolMode
// lets you override that decision. This sample runs ONE agent against the SAME
// prompt three times, changing only ToolMode:
//
//   1. ChatToolMode.Auto              — model decides (this is the default; a
//                                       null ToolMode is treated as Auto).
//   2. ChatToolMode.None              — model sees the tools but must NOT call
//                                       them. Good for an "explain the tools"
//                                       turn, or a cheap text-only reply.
//   3. ChatToolMode.RequireSpecific() — force ONE named function. Returns a
//                                       RequiredChatToolMode whose
//                                       RequiredFunctionName is the tool name.
//
// (A fourth mode, ChatToolMode.RequireAny, forces the model to call *some*
//  tool but lets it pick which — shown in the console summary for completeness.)
//
// We pass ToolMode through ChatClientAgentRunOptions on each RunAsync call, so
// the same agent instance answers under three different policies. After each
// run we inspect response.Messages for FunctionCallContent to report whether a
// tool actually fired.
// ============================================================================

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Chapter 8: Controlling Tool Choice (ChatOptions.ToolMode) ===\n");

// One tool. The tool name the model sees is "get_weather".
AIFunction weatherTool = AIFunctionFactory.Create(GetWeather, name: "get_weather");

ChatClientAgent agent = new(
    chatClient,
    instructions:
        "You are a weather assistant. When a weather tool is available and the " +
        "question is about weather, call it. Otherwise answer from your own knowledge.",
    name: "WeatherAssistant",
    tools: [weatherTool]);

const string prompt = "What's the weather in Seattle?";

// ── Run 1: Auto (the default) ───────────────────────────────────────
await RunUnderModeAsync("Auto (default — model decides)", ChatToolMode.Auto);

// ── Run 2: None (model may not call any tool) ───────────────────────
await RunUnderModeAsync("None (tools visible, calling forbidden)", ChatToolMode.None);

// ── Run 3: RequireSpecific (force the named tool) ───────────────────
await RunUnderModeAsync(
    "RequireSpecific(\"get_weather\") (forced)",
    ChatToolMode.RequireSpecific("get_weather"));

Console.WriteLine("=== Tool-choice demo complete! ===");

// ----------------------------------------------------------------------------
// Runs the shared prompt once under the supplied ToolMode and reports whether
// the tool fired (by scanning the response messages for FunctionCallContent).
// ----------------------------------------------------------------------------
async Task RunUnderModeAsync(string label, ChatToolMode mode)
{
    Console.WriteLine($"--- ToolMode: {label} ---");
    Console.WriteLine($"User: {prompt}");

    // ToolMode lives on ChatOptions; pass per-run via ChatClientAgentRunOptions.
    ChatClientAgentRunOptions runOptions = new(new ChatOptions { ToolMode = mode });

    AgentSession session = await agent.CreateSessionAsync(cts.Token);
    AgentResponse response = await agent.RunAsync(
        prompt, session, runOptions, cts.Token);

    bool toolFired = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<FunctionCallContent>()
        .Any();

    Console.WriteLine($"Tool called: {(toolFired ? "yes" : "no")}");
    Console.WriteLine($"Agent: {response.Text}\n");
}

// ----------------------------------------------------------------------------
// The single tool. Same simulated data as Ch08.WeatherTool for consistency.
// ----------------------------------------------------------------------------
[Description("Gets the current weather conditions for a specified city.")]
static string GetWeather(
    [Description("The name of the city, e.g. 'Seattle', 'Tokyo', 'London'.")] string city)
{
    Dictionary<string, string> data = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Seattle"] = "12°C, overcast with light rain, humidity 78%",
        ["Tokyo"] = "22°C, partly cloudy, humidity 65%",
        ["London"] = "14°C, foggy, humidity 82%",
    };

    return data.TryGetValue(city, out string? report)
        ? $"Weather in {city}: {report}."
        : $"Weather data for '{city}' is not available in the demo dataset.";
}
