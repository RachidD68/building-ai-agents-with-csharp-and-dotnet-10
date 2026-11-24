# AutoGen to MAF Migration

AutoGen's agents, teams and stop rules mapped to the MAF 1.28 types this book uses, and the behaviour to test again after the port.

> **Where this comes from.** This guide was Appendix D in the drafts of the second edition of *Building AI Agents with C# and .NET 10*; the book now prints a two-page summary, Appendix C, and points here. The AutoGen side is text from the first edition (its Appendix C), named in words only: this repository holds no Python to check a listing against, and the AutoGen names are AgentChat names as the guide uses them, not checked against a particular release; check them against the version you run. The MAF side is verified at 1.28, the package set this repository pins (`Directory.Packages.props`; the book's Appendix B lists it). Every C# listing below is copied verbatim from this repository, with `// ...` marking lines left out, and every output block is verbatim lines of an offline self-test (no model).

Chapter numbers below are the second edition's.

This guide is for a developer with AutoGen code, usually Python on the AgentChat API, who wants the same system on .NET. The names carry over better than the behaviour, so port each team together with a test that pins how it starts and stops.

## The Concept Map

| AutoGen | MAF 1.28 | Where |
|---|---|---|
| AssistantAgent | ChatClientAgent over an IChatClient: instructions, name, description, tools | Chapter 4 |
| model_client | IChatClient from Microsoft.Extensions.AI, provider chosen by configuration | Chapter 3 |
| A Python function as a tool | AIFunctionFactory.Create over a method with [Description] | Chapter 7 |
| AgentTool | agent.AsAIFunction(): the agent's name and description become the tool's | Chapter 13 |
| RoundRobinGroupChat | CreateGroupChatBuilderWith and RoundRobinGroupChatManager | Chapter 13 |
| SelectorGroupChat | A GroupChatManager subclass, or an orchestrator agent with specialists as tools | Chapter 13 |
| Swarm, HandoffMessage | CreateHandoffBuilderWith(entry).WithHandoffs(from, targets) | Chapter 13 |
| MagenticOneGroupChat | MagenticWorkflowBuilder(manager) (online-only sample, no captured run) | Chapter 13 |
| Termination conditions (group chat) | MaximumIterationCount and a stop function on the manager | Chapter 13 |
| UserProxyAgent | A RequestPort in a workflow, or approval of one tool call | Chapter 14, Chapter 8 |
| GraphFlow | WorkflowBuilder with AddEdge, AddSwitch and AddFanInBarrierEdge | Chapter 14 |
| save_state, load_state | AgentSessionStore for a session, CheckpointManager for a run | Chapter 6, Chapter 15 |
| run, run_stream | RunAsync, RunStreamingAsync; InProcessExecution for a workflow | Chapter 4, Chapter 14 |

## Agents and Tools

AutoGen takes a tool's description from the docstring and its schema from the type hints. MAF takes them from `[Description]` attributes and the C# signature, and leaves the `CancellationToken` out of the schema (`src/Ch07.Tools/Ch07.WeatherTool/Program.cs`):

```csharp
AIFunction weatherTool = AIFunctionFactory.Create(GetWeatherAsync);
// ...
[Description("Gets the current weather conditions for a specified city, including temperature, humidity, and description.")]
static async Task<string> GetWeatherAsync(
    [Description("The name of the city to get weather for (e.g., 'Seattle', 'Tokyo', 'London')")] string city,
    CancellationToken cancellationToken)
```

The model picks a tool by its description, so write it as routing text, and decide what each tool may touch before you port it (Chapter 7, Chapter 8). If the tools are Python and too many to port, keep them behind an MCP server (Chapter 9) and the C# agent sees them like any other tool.

## Teams Become Workflows

In MAF each built-in orchestration is a builder that returns a `Workflow`, run through `InProcessExecution` with the transcript in a `WorkflowOutputEvent`. One trap first: an AutoGen task is usually a string, and in 1.28 a string reaches `BuildConcurrent`'s agents as an empty conversation. Pass a `ChatMessage` to every workflow; `Ch13.MultiAgent.Tests` reproduces the defect.

### RoundRobinGroupChat

A message limit combined with a text condition such as APPROVED becomes two rules on the manager: `MaximumIterationCount` is the hard cap; the stop function passed to the manager (the lambda at the end, which `SpeakersAsync` hands on as `stop`) ends the chat early. The run in Chapter 13 shows the cap stopping at six turns and the condition at the first APPROVED. From `src/Ch13.MultiAgentTopology/Ch13.MultiAgent.Tests/Program.cs`:

```csharp
    async Task<List<string>> SpeakersAsync(Func<RoundRobinGroupChatManager, IEnumerable<ChatMessage>, CancellationToken, ValueTask<bool>>? stop)
        // ...
        Workflow workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents, stop) { MaximumIterationCount = 6 })
            .AddParticipants(writer, reviewer)
            .Build();
    // ...
    List<string> early = await SpeakersAsync((_, history, _) =>
        ValueTask.FromResult(history.LastOrDefault()?.Text.Contains("APPROVED") == true));
```

Set both, and count the limit again from a test transcript rather than copying it. For `SelectorGroupChat`, subclass `GroupChatManager`, or let an orchestrator agent pick, which Chapter 13 measures against the other shapes.

### Swarm

A Swarm agent lists its handoff targets and the model emits the handoff. In MAF the builder declares the edges, and each becomes a tool named `handoff_to_<n>` with the target's description (same file):

```csharp
    Workflow workflow = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)
        .WithHandoffs(triage, [billing, supervisor])
        .Build();
```

```text
  PASS  triage is offered one handoff tool per declared edge
  PASS  billing, with no outgoing edge, is offered no handoff tool
  PASS  with no edge, Billing's transfer instruction has no tool to call, and the Supervisor never runs
```

Billing's scripted reply tells it to transfer to the Supervisor, and the last line shows the transfer never happens. The graph decides which transfers exist, whatever the model writes. The full self-test run is [../outputs/Ch13.MultiAgent.Tests.self-test.txt](../outputs/Ch13.MultiAgent.Tests.self-test.txt).

## UserProxyAgent and GraphFlow Become One Graph

MAF keeps the person outside the agents. A `RequestPort` is a typed node: the run pauses with a `RequestInfoEvent` and your code answers with `SendResponseAsync(request.CreateResponse(reply))`. The graph around it, the claim review of Chapter 14, is what a GraphFlow becomes (`src/Ch14.Workflows/Ch14.ApprovalWorkflow/ClaimReview.cs`):

```csharp
        return new WorkflowBuilder(classify)
            .AddSwitch(classify, sw => sw
                .AddCase<RoutedClaim>(c => c is { Route: ClaimRoute.Review }, [policy, receipt])
                .WithDefault([returned]))
            .AddFanInBarrierEdge([policy, receipt], collect)
            .AddEdge(collect, write)
            .AddEdge(write, port)
            .AddSwitch(port, sw => sw
                .AddCase<ReviewReply>(r => r is { Decision: ReviewDecision.Approve }, [approved])
                .AddCase<ReviewReply>(r => r is { Decision: ReviewDecision.Revise }, [revise])
                .AddCase<ReviewReply>(r => r is { Decision: ReviewDecision.Reject }, [rejected])
                .WithDefault([undecided]))
            .AddEdge(revise, write)
            .WithOutputFrom(returned, approved, revise, rejected, undecided)
            .WithName("ContosoClaimReview")
            .Build();
```

A user proxy reads free text. Here free text that isn't a decision sends no reply, so the run waits at the port, and a reply whose decision is none of the three cases takes the switch's default. Only a ReviewDecision of Approve approves:

```text
  PASS  unknown word "approved" -> no reply sent, run PendingRequests, no outcome
  PASS  default(ReviewDecision), JSON {} and decision 7 are not Approve -> switch default -> not-approved
```

The full self-test run is [../outputs/Ch14.WorkflowTests.self-test.txt](../outputs/Ch14.WorkflowTests.self-test.txt).

## A Migration Order

This is the order I'd port in, so each step is tested before the next one depends on it:

1. Port the tools first and test them without a model: they are the code that changes state.
2. Port each agent as a `ChatClientAgent` with an offline test over a scripted `IChatClient`, as in `Ch04.AgentTests`.
3. Rebuild each team and pin its stop rules, edges and input type in an offline test.
4. Replace each user proxy with a port, and test that silence decides nothing.
5. Move saved state last. The book has no converter from AutoGen's saved state, so finish open conversations on the old system.

Then rerun your evaluation set (Chapter 21) on the ported system before you retire the old one. Another model client and orchestration change the answers even when every name lines up.

See also [Semantic Kernel to MAF Migration](./semantic-kernel-to-maf.md).
