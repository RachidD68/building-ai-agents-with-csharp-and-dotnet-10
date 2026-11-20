using System.Text.Json.Serialization;

namespace Ch18.SseStatusStream;

/// <summary>
/// The operation's state. The application sets it, after it observed what
/// happened; streamed text never does. OutcomeUnknown is not terminal: the
/// effect may or may not have happened, and only reconciliation decides.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationState>))]
public enum OperationState { Running, AwaitingApproval, Completed, Failed, Cancelled, Expired, OutcomeUnknown }

public static class OperationStates
{
    /// <summary>The only states that may show "done" to the user.</summary>
    public static bool IsTerminal(OperationState? state) =>
        state is OperationState.Completed or OperationState.Failed or OperationState.Cancelled or OperationState.Expired;
}

/// <summary>The SSE event types. One meaning per type; a client never guesses from the text.</summary>
public static class EventTypes
{
    public const string Delta = "delta";                 // a piece of the response's text
    public const string ResponseDone = "response.done";  // the response stream ended; says nothing about the operation
    public const string Tool = "tool";                   // tool status (started, finished)
    public const string State = "state";                 // the operation's state, set by the application
    public const string CancelRequested = "cancel.requested";
    public const string Snapshot = "snapshot";           // the whole view at a cursor (sent after a gap)
    public const string Resync = "resync";               // the client fell behind: fetch a snapshot, reconnect
}

/// <summary>
/// The event envelope. EventId identifies one event forever; Cursor is its
/// place in the conversation's log (the SSE id, sent back as Last-Event-ID);
/// RunId says which run it belongs to; Version is the envelope's schema
/// version; Epoch names the log instance, so a client can tell that a
/// restarted server numbers from 1 again. A snapshot carries the text and the
/// operation state at its cursor.
/// </summary>
public sealed record StatusEvent(
    string EventId, string ConversationId, string RunId, string Type, int Version, long Cursor,
    string? Text = null, OperationState? State = null, string? Reason = null, string? Epoch = null)
{
    public const int SchemaVersion = 1;
}
