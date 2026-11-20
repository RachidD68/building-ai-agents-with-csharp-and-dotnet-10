using Microsoft.Agents.AI;

namespace Ch06.SessionOwnership;

/// <summary>What a chat client sends. Everything in it is the client's claim.</summary>
public sealed record ChatRequest(string ConversationId, string Text, string? TenantId = null, string? SubjectId = null);

/// <summary>
/// The broken version: it builds the store key from the payload. A caller who
/// writes another tenant and subject into the request loads their conversation.
/// </summary>
public sealed class PayloadKeyedConversations(AIAgent agent, AgentSessionStore store)
{
    public async Task<AgentSession?> LoadAsync(ChatRequest request, CancellationToken ct = default) =>
        await store.GetSessionAsync(agent,
            ConversationKeys.For(new AuthenticatedCaller(request.TenantId!, request.SubjectId!), request.ConversationId), ct);
}

/// <summary>
/// The fix: the key comes from the authenticated caller. The payload carries
/// the conversation id and the text, and its tenant and subject are ignored.
/// </summary>
public sealed class OwnedConversations(AIAgent agent, AgentSessionStore store)
{
    public async Task<string> SendAsync(AuthenticatedCaller caller, ChatRequest request, CancellationToken ct = default)
    {
        var key = ConversationKeys.For(caller, request.ConversationId);
        AgentSession session = await store.GetOrCreateSessionAsync(agent, key, ct);
        AgentResponse response = await agent.RunAsync(request.Text, session, cancellationToken: ct);
        await store.SaveSessionAsync(agent, key, session, ct);   // SessionConflictException if another writer won
        return response.Text;
    }

    public async Task<AgentSession?> LoadAsync(AuthenticatedCaller caller, ChatRequest request, CancellationToken ct = default) =>
        await store.GetSessionAsync(agent, ConversationKeys.For(caller, request.ConversationId), ct);

    /// <summary>"Forget this conversation": owner only, and repeating it is a no-op.</summary>
    public ValueTask ForgetAsync(AuthenticatedCaller caller, string conversationId, CancellationToken ct = default) =>
        store.DeleteSessionAsync(agent, ConversationKeys.For(caller, conversationId), ct);
}
