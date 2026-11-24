# Semantic Kernel to MAF Migration

What each Semantic Kernel idea is called in MAF, the rewrites you'll make most, and the traps to check first.

> **Where this comes from.** This guide was Appendix C in the drafts of the second edition of *Building AI Agents with C# and .NET 10*; the book now prints a two-page summary, Appendix C, and points here. The Semantic Kernel side is text from the first edition (its Appendix B). The MAF side is verified at 1.28, the package set this repository pins (`Directory.Packages.props`; the book's Appendix B lists it). The first edition's map named no SK version; every SK name printed here was checked against Semantic Kernel 1.77.0 (`Microsoft.SemanticKernel.Core` and `.Abstractions`), except the planner and process rows. On another release, check the names against your own packages.

Chapter numbers below are the second edition's.

Semantic Kernel came before the Microsoft Agent Framework in the .NET AI stack, and most of its ideas survived under new names. If you own an SK codebase, this is the translation sheet.

## The Concept Map

| Semantic Kernel | MAF 1.28 packages | What changes |
|---|---|---|
| `Kernel`, `IChatCompletionService` | `IChatClient` + `ChatClientAgent` | The Kernel bundled model, plugins and services. MAF splits them: a provider IChatClient talks to the model, the agent adds instructions, tools and a session (Chapter 4). |
| `[KernelFunction]` method | `AIFunctionFactory.Create(...)` | Returns an AIFunction; [Description] carries over unchanged (Chapter 7). |
| `KernelPlugin` | a list of `AITool` | No container type. You pass the tools to the agent and name them yourself. |
| `KernelArguments` | `AIFunctionArguments` | In SK 1.77.0, KernelArguments already derives from AIFunctionArguments. |
| `FunctionChoiceBehavior` | `ChatOptions.ToolMode` | ChatToolMode.Auto, RequireAny, RequireSpecific(name) or None. |
| `ChatHistory` | `AgentSession` + `ChatHistoryProvider` | The session owns the conversation; SerializeSessionAsync persists it (Chapter 6). |
| `IMemoryStore` | `VectorStoreCollection<TKey, TRecord>` | Typed records in Microsoft.Extensions.VectorData (Chapter 10). IMemoryStore is experimental (SKEXP0001) in 1.77.0. |
| `IFunctionInvocationFilter`, `IAutoFunctionInvocationFilter` | `AIAgentBuilder.Use(...)` function middleware | Runs around every tool call. Approval moves to ApprovalRequiredAIFunction + UseToolApproval (Chapter 8). |
| `IPromptRenderFilter` | `DelegatingChatClient` via `ChatClientBuilder.Use` | Sees every message list before the model does (Chapter 20). |
| Stepwise and Handlebars planners | the tool-calling loop; explicit orchestration | No planner to port. Stepwise behaviour is the agent's default loop (Chapter 7); explicit orchestration is Chapter 13. |
| process framework | `WorkflowBuilder`, executors, edges | The plan on the page, where a test can pin it, with checkpoints and human review (Chapter 14, Chapter 15). |

## A Plugin Becomes a Tool

This is the first edition's SK plugin, a class whose methods carry `[KernelFunction]`, registered on the kernel under a plugin name:

```csharp
public sealed class WeatherPlugin
{
    [KernelFunction, Description("Gets current weather")]
    public string GetWeather(
        [Description("City name")] string city) => ...;
}

kernel.Plugins.AddFromObject(new WeatherPlugin(), "weather");
```

And this is the same tool in `Ch07.WeatherTool` (`src/Ch07.Tools/Ch07.WeatherTool/Program.cs`), from Chapter 7:

```csharp
[Description("Gets the current weather conditions for a specified city, including temperature, humidity, and description.")]
static async Task<string> GetWeatherAsync(
    [Description("The name of the city to get weather for (e.g., 'Seattle', 'Tokyo', 'London')")] string city,
    CancellationToken cancellationToken)
// ...
AIFunction weatherTool = AIFunctionFactory.Create(GetWeatherAsync);
ChatClientAgent agent = new(
    chatClient,
    // ...
    tools: [weatherTool]);
```

There's no plugin to register, only an `AIFunction` in the agent's `tools` list. The new `CancellationToken` is filled by the tool loop and never shown to the model; add one to every async plugin method while you're there.

## Keep the Plugin for Now

You don't have to rewrite every plugin before the first MAF agent ships. In SK 1.77.0, `KernelFunction` is itself an `AIFunction` (through `FullyQualifiedAIFunction`), and `KernelPlugin.AsAIFunctions` hands a whole plugin over as tools:

```csharp
KernelPlugin weather = KernelPluginFactory.CreateFromObject(new WeatherPlugin(), "weather");

// Pass a Kernel: without one the function builds its own from the run's services
// and fails, because no KernelPlugin is registered there.
ChatClientAgent agent = new(
    chatClient,
    instructions: "You are a helpful weather assistant.",
    tools: [.. weather.AsAIFunctions(new Kernel())]);
```

Leave the Kernel argument out and the agent gets "Error: Function failed." as the tool result. Function middleware still sees the real exception, an InvalidOperationException for a missing `IEnumerable<KernelPlugin>` service, but the tool loop hides it from the model. With the Kernel, run against a scripted chat client (no model), the agent prints:

```text
tool name the model sees: weather_GetWeather
  before weather_GetWeather([city, Seattle])
  after  weather_GetWeather -> 12 C and overcast in Seattle
agent: It is 12 C and overcast in Seattle.
```

The full captured run, including the run without a Kernel, is in [outputs/semantic-kernel-bridge.txt](./outputs/semantic-kernel-bridge.txt).

## Filters Become Middleware

An SK function filter implements `OnFunctionInvocationAsync(context, next)` on the kernel. The MAF counterpart is a callback on the agent builder with the same shape. The before and after lines above came from this one:

```csharp
AIAgent audited = agent.AsBuilder()
    .Use(async (callingAgent, context, next, cancellationToken) =>
    {
        Console.WriteLine($"  before {context.Function.Name}({string.Join(", ", context.Arguments)})");
        object? result = await next(context, cancellationToken);
        Console.WriteLine($"  after  {context.Function.Name} -> {result}");
        return result;
    })
    .Build();
```

A prompt-render filter moves one layer down: a DelegatingChatClient, stacked with ChatClientBuilder.Use, sees every message list on its way to the model, but no tool executions: a rule about what a tool may do belongs on the tool or its approval (Chapter 8). Chapter 20 builds a three-stage pipeline that way. One SK habit doesn't survive: a chat-client middleware has two entry points, GetResponseAsync and GetStreamingResponseAsync, and a guard that overrides one leaves the other open.

## Migrating in Phases

Each phase ships on its own. **First,** route every model call through IChatClient and leave the plugins alone. **Second,** move agents to ChatClientAgent with bridged plugins, and freeze the tool names. **Third,** rewrite plugins as plain methods for AIFunctionFactory.Create, move ChatHistory into sessions, and re-ingest IMemoryStore data into a VectorStoreCollection. **Last,** turn filters into middleware and planners into loops or workflows. [AutoGen to MAF Migration](./autogen-to-maf.md) covers the same move from AutoGen.

## Pitfalls

- **Name collisions.** Import Microsoft.SemanticKernel and Microsoft.Extensions.AI in one file and FunctionCallContent, FunctionResultContent, FunctionInvocationContext and TextContent become ambiguous, because SK 1.77.0 ships its own types with those names (AIContext and AIContextProvider clash with Microsoft.Agents.AI). Alias the ones you mean during the bridge phase.
- **Renamed tools.** The bridged tool is weather_GetWeather (first output line above). On a static local function such as Chapter 7's GetWeatherAsync, AIFunctionFactory.Create derives a name like _Main_g_GetWeather_0_2, with an ordinal that depends on the file. Prompts, evaluation cases and allow-lists keyed on the old name miss it, so pass name: and freeze the names before you switch.
- **Two system prompts.** A migrated ChatHistory often starts with the system message, and the agent sends its own instructions on every run, so strip the old one on import.
- **Experimental IDs.** SK marks its experiments SKEXP, MAF uses its own IDs such as MAAI001. A suppression copied across silences nothing, so look up each warning again.
