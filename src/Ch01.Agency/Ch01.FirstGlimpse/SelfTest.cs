using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch01.FirstGlimpse;

/// <summary>
/// --self-test: the same agent as Program.cs, run against the offline client.
/// It checks the shape the chapter describes, not the quality of any answer:
/// the persona travels with every call, the session carries the conversation,
/// a new session starts empty, and the text comes back unchanged.
/// </summary>
internal static class SelfTest
{
    const string Persona = "You are a friendly assistant for .NET developers. Be concise.";
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Your First Glimpse of an Agent (self-test) ===");
        var client = new OfflineChatClient();
        ChatClientAgent agent = new(client, instructions: Persona, name: "FirstGlimpse");

        AgentSession session = await agent.CreateSessionAsync();
        AgentResponse first = await agent.RunAsync("In one sentence, what is an AI agent?", session);
        Expect("one RunAsync makes exactly one model call", client.Calls.Count == 1);
        Expect("the response text is the model's text, unchanged", first.Text == OfflineChatClient.CannedReply);
        Expect("the persona reaches the model on the call", HasPersona(client.Calls[0]));
        Expect("the agent keeps its name", agent.Name == "FirstGlimpse");

        await agent.RunAsync("And what is a workflow?", session);
        var second = client.Calls[1].Messages;
        Expect("the same session sends the earlier question and answer again",
            second.Any(m => m.Role == ChatRole.User && m.Text.Contains("what is an AI agent")) &&
            second.Any(m => m.Role == ChatRole.Assistant && m.Text == OfflineChatClient.CannedReply));
        Expect("the persona is sent again on the second call", HasPersona(client.Calls[1]));

        AgentSession fresh = await agent.CreateSessionAsync();
        await agent.RunAsync("Hello", fresh);
        Expect("a new session starts without the earlier conversation",
            !client.Calls[2].Messages.Any(m => m.Text.Contains("what is an AI agent")));

        Console.WriteLine($"RESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static bool HasPersona((List<ChatMessage> Messages, ChatOptions? Options) call) =>
        call.Options?.Instructions == Persona ||
        call.Messages.Any(m => m.Role == ChatRole.System && m.Text.Contains(Persona));

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}
