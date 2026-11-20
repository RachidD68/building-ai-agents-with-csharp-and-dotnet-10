using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch16.SkillLifecycle;

/// <summary>
/// Reads the skills in a folder outside any run (for --approve). A source needs an agent
/// and a session in its context, so this gives it an agent with no model behind it.
/// </summary>
public static class SkillDiscovery
{
    public static async Task<IList<AgentSkill>> FromFolderAsync(string root, CancellationToken ct = default)
    {
        ChatClientAgent reviewer = new(new NoModel(), name: "skill-review");
        AgentSkillsSourceContext context = new(reviewer, await reviewer.CreateSessionAsync(ct));
        return await new AgentFileSkillsSource(root).GetSkillsAsync(context, ct);
    }

    private sealed class NoModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("Skill review calls no model.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Skill review calls no model.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
