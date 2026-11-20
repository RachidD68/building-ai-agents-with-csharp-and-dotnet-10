using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch18.SseStatusStream;

/// <summary>
/// Streams one agent run into the log as typed events: text as deltas, tool
/// calls as tool status, the end of the response as response.done. It never
/// writes a terminal state. What the operation became is the application's
/// to report (OperationLog.SetState), after it observed the outcome.
/// </summary>
public static class StatusRunner
{
    public static async Task<string> StreamResponseAsync(OperationLog log, string conversationId, string runId,
        AIAgent agent, AgentSession session, string message, CancellationToken ct = default)
    {
        System.Text.StringBuilder text = new();
        Dictionary<string, string> calls = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(message, session, cancellationToken: ct))
        {
            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    case TextContent t when t.Text.Length > 0:
                        log.Delta(conversationId, runId, t.Text);
                        text.Append(t.Text);
                        break;
                    case FunctionCallContent call:
                        calls[call.CallId] = call.Name;
                        log.Tool(conversationId, runId, call.Name, "started");
                        break;
                    case FunctionResultContent result:
                        log.Tool(conversationId, runId, calls.GetValueOrDefault(result.CallId, result.CallId), "finished");
                        break;
                }
            }
        }
        log.ResponseDone(conversationId, runId);
        return text.ToString();
    }
}
