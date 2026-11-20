using System.Text;

namespace Ch18.SseStatusStream;

/// <summary>
/// What the client shows, rebuilt only from events. Four rules:
/// an event id is applied once; a cursor at or before the view's is discarded;
/// a cursor that skips one means a gap (resync from a snapshot); an event of
/// another run than the current one never changes the view. "Done" is shown
/// only for a terminal state of the current run, never for response.done.
/// </summary>
public sealed class StatusView
{
    private readonly HashSet<string> _applied = [];
    private readonly StringBuilder _text = new();

    public long Cursor { get; private set; }
    public string? RunId { get; private set; }
    public OperationState? State { get; private set; }
    public string? Reason { get; private set; }
    public string? ToolStatus { get; private set; }
    public bool CancelPending { get; private set; }
    public bool ResponseDone { get; private set; }
    public bool NeedsResync { get; private set; }
    public int Discarded { get; private set; }
    public string Text => _text.ToString();
    public IReadOnlyCollection<string> AppliedIds => _applied;

    public bool ShowsDone => OperationStates.IsTerminal(State);

    public void Apply(StatusEvent e)
    {
        if (e.Type == EventTypes.Snapshot)
        {
            if (e.Cursor < Cursor) { Discarded++; return; }   // an older snapshot than what we have
            (Cursor, RunId, State, Reason, NeedsResync) = (e.Cursor, e.RunId, e.State, e.Reason, false);
            _text.Clear().Append(e.Text);
            return;
        }
        if (e.Cursor <= Cursor || !_applied.Add(e.EventId)) { Discarded++; return; }
        if (e.Cursor > Cursor + 1)
        {
            _applied.Remove(e.EventId);
            NeedsResync = true;   // a gap: something between was missed
            return;
        }
        Cursor = e.Cursor;
        if (e.Type == EventTypes.Resync) { NeedsResync = true; return; }
        if (e.Type == EventTypes.State && e.State == OperationState.Running && e.RunId != RunId)
        {
            (RunId, State, Reason, ToolStatus, CancelPending, ResponseDone) = (e.RunId, e.State, e.Reason, null, false, false);
            _text.Clear();
            return;
        }
        if (e.RunId != RunId) { Discarded++; return; }   // a late event of an older run
        switch (e.Type)
        {
            case EventTypes.Delta: _text.Append(e.Text); break;
            case EventTypes.ResponseDone: ResponseDone = true; break;
            case EventTypes.Tool: ToolStatus = $"{e.Text}: {e.Reason}"; break;
            case EventTypes.CancelRequested: CancelPending = true; break;
            case EventTypes.State:
                (State, Reason) = (e.State, e.Reason);
                CancelPending = false;
                break;
        }
    }

    /// <summary>One line for the console client.</summary>
    public string Describe() =>
        $"run {RunId} | {(ShowsDone ? "DONE " : "")}{State?.ToString() ?? "-"} ({Reason}) | cursor {Cursor}" +
        (CancelPending ? " | cancel requested" : "") + (NeedsResync ? " | RESYNC" : "");
}

/// <summary>
/// The first version every team writes, kept to reproduce the failures: it
/// appends every delta it receives, treats response.done or any Completed as
/// done, and shows Cancelled as soon as the user clicks.
/// </summary>
public sealed class NaiveView
{
    private readonly StringBuilder _text = new();
    public string Text => _text.ToString();
    public bool ShowsDone { get; private set; }
    public string Shown { get; private set; } = "";

    public void Apply(StatusEvent e)
    {
        switch (e.Type)
        {
            case EventTypes.Snapshot: _text.Clear().Append(e.Text); break;
            case EventTypes.Delta: _text.Append(e.Text); break;
            case EventTypes.ResponseDone: ShowsDone = true; Shown = "Done"; break;
            case EventTypes.State when e.State == OperationState.Completed: ShowsDone = true; Shown = "Completed"; break;
        }
    }

    public void ClickCancel()
    {
        ShowsDone = true;
        Shown = "Cancelled";
    }
}
