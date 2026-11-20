using Ch06.SessionOwnership;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Contoso.OwnedConversation;

/// <summary>Who may use the assistant, as the host's identity system would answer.</summary>
public sealed class EmployeeDirectory
{
    private readonly HashSet<(string Tenant, string Subject)> _known = [];

    public void Add(string tenant, string subject) => _known.Add((tenant, subject));

    /// <summary>The caller as authentication would produce it; unknown people are refused.</summary>
    public AuthenticatedCaller Authenticate(string tenant, string subject) =>
        _known.Contains((tenant, subject))
            ? new AuthenticatedCaller(tenant, subject)
            : throw new UnauthorizedAccessException($"{tenant}/{subject} is not a known employee");
}

public enum TurnOutcome { Answered, Conflict }

public sealed record TurnResult(TurnOutcome Outcome, string Text);

/// <summary>
/// The Contoso expense assistant with an owned conversation. Identity comes
/// from the authenticated caller; the request carries only the conversation id
/// and the text. A save that loses to another tab is reported, not hidden: the
/// client reloads the conversation and sends the line again.
/// </summary>
public sealed class ExpenseConversations
{
    public const string Instructions =
        "You are the Contoso expense assistant. You help an employee prepare an expense claim. " +
        "Answer only from what the employee told you in this conversation. Be brief.";

    private readonly OwnedConversations _owned;

    public ExpenseConversations(IChatClient model, string databasePath)
    {
        History = new InMemoryChatHistoryProvider();
        Agent = new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "ContosoExpenseAssistant",
            ChatHistoryProvider = History,
            ChatOptions = new ChatOptions { Instructions = Instructions },
        });
        Store = new SqliteSessionStore(databasePath);
        _owned = new OwnedConversations(Agent, Store);
    }

    public ChatClientAgent Agent { get; }
    public InMemoryChatHistoryProvider History { get; }
    public SqliteSessionStore Store { get; }

    public async Task<TurnResult> SendAsync(AuthenticatedCaller caller, ChatRequest request, CancellationToken ct = default)
    {
        try
        {
            return new(TurnOutcome.Answered, await _owned.SendAsync(caller, request, ct));
        }
        catch (SessionConflictException)
        {
            return new(TurnOutcome.Conflict, "This conversation changed in another window. Reload it and send your message again.");
        }
    }

    /// <summary>The caller's stored messages for a conversation (0 when there is none).</summary>
    public async Task<int> MessageCountAsync(AuthenticatedCaller caller, string conversationId, CancellationToken ct = default)
    {
        AgentSession? session = await _owned.LoadAsync(caller, new ChatRequest(conversationId, ""), ct);
        return session is null ? 0 : History.GetMessages(session).Count;
    }

    public ValueTask ForgetAsync(AuthenticatedCaller caller, string conversationId, CancellationToken ct = default) =>
        _owned.ForgetAsync(caller, conversationId, ct);
}
