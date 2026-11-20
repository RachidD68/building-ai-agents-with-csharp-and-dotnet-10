using System.Text;
using System.Threading.Channels;

namespace Ch18.SseStatusStream;

/// <summary>
/// The server's record of what each conversation's clients may see: an
/// append-only log per conversation, a cursor per event, the last
/// <c>retain</c> events kept for replay, and a bounded queue per connected
/// client. One lock per log: appends, replays and subscriptions see the same
/// order, so a client that subscribes while events arrive misses none and
/// gets none twice.
/// </summary>
public sealed class OperationLog(int retain = 256, int clientQueue = 64)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Conversation> _conversations = [];

    public int ClientQueue => clientQueue;

    /// <summary>This log instance. The log is in memory: a restarted server numbers from 1 again, under a new epoch.</summary>
    public string Epoch { get; } = "log_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A new run: its own id, Running, an empty response text.</summary>
    public string StartRun(string conversationId)
    {
        string runId = "run_" + Guid.NewGuid().ToString("N")[..12];
        lock (_gate)
        {
            Conversation c = Get(conversationId);
            c.RunId = runId;
            c.Text.Clear();
            Append(c, runId, EventTypes.State, state: OperationState.Running, reason: "run_started");
        }
        return runId;
    }

    public StatusEvent Delta(string conversationId, string runId, string text) => Add(conversationId, runId, EventTypes.Delta, text);

    public StatusEvent ResponseDone(string conversationId, string runId) => Add(conversationId, runId, EventTypes.ResponseDone);

    public StatusEvent Tool(string conversationId, string runId, string tool, string status) =>
        Add(conversationId, runId, EventTypes.Tool, tool, reason: status);

    /// <summary>A request, not a result: the state changes only when the application reports what happened.</summary>
    public StatusEvent CancelRequested(string conversationId, string runId) => Add(conversationId, runId, EventTypes.CancelRequested);

    /// <summary>
    /// The application's confirmed state of a run's operation. The only way to a terminal
    /// state, and a terminal state is final: a later state for that run is refused (null).
    /// </summary>
    public StatusEvent? SetState(string conversationId, string runId, OperationState state, string reason)
    {
        lock (_gate)
        {
            Conversation c = Get(conversationId);
            if (c.Ended.Contains(runId)) return null;
            if (OperationStates.IsTerminal(state)) c.Ended.Add(runId);
            return Append(c, runId, EventTypes.State, state: state, reason: reason);
        }
    }

    /// <summary>The whole view at the current cursor: the current run's text and the operation's state.</summary>
    public StatusEvent Snapshot(string conversationId)
    {
        lock (_gate) return SnapshotOf(Get(conversationId));
    }

    /// <summary>
    /// Everything after <paramref name="after"/>, then live events, in one step.
    /// A cursor older than the retained window, or past the log's end (the client
    /// read an earlier instance of this log), gets a snapshot instead of a replay.
    /// </summary>
    public Subscription Subscribe(string conversationId, long after)
    {
        lock (_gate)
        {
            Conversation c = Get(conversationId);
            long oldest = c.Events.Count == 0 ? c.Next : c.Events[0].Cursor;
            IReadOnlyList<StatusEvent> backlog = after >= oldest - 1 && after < c.Next
                ? [.. c.Events.Where(e => e.Cursor > after)]
                : [SnapshotOf(c)];
            Subscription s = new(this, c, clientQueue) { Backlog = backlog, RunId = c.RunId };
            c.Subscribers.Add(s);
            return s;
        }
    }

    public long Cursor(string conversationId)
    {
        lock (_gate) return Get(conversationId).Next - 1;
    }

    private StatusEvent Add(string conversationId, string runId, string type, string? text = null,
        OperationState? state = null, string? reason = null)
    {
        lock (_gate)
        {
            Conversation c = Get(conversationId);
            if (type == EventTypes.Delta && runId == c.RunId) c.Text.Append(text);
            return Append(c, runId, type, text, state, reason);
        }
    }

    // Under _gate. Every subscriber gets the event or, when its queue is full, is
    // dropped with its writer completed: it reads a resync and reconnects.
    private StatusEvent Append(Conversation c, string runId, string type, string? text = null,
        OperationState? state = null, string? reason = null)
    {
        StatusEvent e = new($"{c.Id}:{Epoch}:{c.Next}", c.Id, runId, type, StatusEvent.SchemaVersion, c.Next, text, state, reason, Epoch);
        c.Next++;
        if (runId == c.RunId && type == EventTypes.State) (c.State, c.Reason) = (state, reason);
        c.Events.Add(e);
        if (c.Events.Count > retain) c.Events.RemoveAt(0);
        foreach (Subscription s in c.Subscribers.ToArray())
        {
            if (s.Queue.Writer.TryWrite(e)) s.NotePeak();
            else
            {
                s.Overflowed = true;
                s.Queue.Writer.TryComplete();
                c.Subscribers.Remove(s);
            }
        }
        return e;
    }

    private StatusEvent SnapshotOf(Conversation c) =>
        new($"{c.Id}:{Epoch}:snapshot:{c.Next - 1}", c.Id, c.RunId ?? "", EventTypes.Snapshot, StatusEvent.SchemaVersion,
            c.Next - 1, c.Text.ToString(), c.State, c.Reason, Epoch);

    // Never evicted: memory grows with the number of conversations. A production log
    // needs eviction or durable storage.
    private Conversation Get(string id)
    {
        if (!_conversations.TryGetValue(id, out Conversation? c)) _conversations[id] = c = new Conversation(id);
        return c;
    }

    internal void Unsubscribe(Subscription s, Conversation c)
    {
        lock (_gate) c.Subscribers.Remove(s);
    }

    internal sealed class Conversation(string id)
    {
        public string Id { get; } = id;
        public long Next { get; set; } = 1;
        public List<StatusEvent> Events { get; } = [];
        public List<Subscription> Subscribers { get; } = [];
        public StringBuilder Text { get; } = new();
        public string? RunId { get; set; }
        public OperationState? State { get; set; }
        public string? Reason { get; set; }
        public HashSet<string> Ended { get; } = [];   // runs that reached a terminal state
    }
}

/// <summary>One connected client: its backlog, its bounded live queue, and whether it fell behind.</summary>
public sealed class Subscription
{
    private readonly OperationLog _log;
    private readonly OperationLog.Conversation _conversation;
    private int _peak;

    internal Subscription(OperationLog log, OperationLog.Conversation conversation, int capacity)
    {
        _log = log;
        _conversation = conversation;
        Queue = Channel.CreateBounded<StatusEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // TryWrite fails when full; the log drops the client
            SingleReader = true,
        });
    }

    public IReadOnlyList<StatusEvent> Backlog { get; internal init; } = [];
    public Channel<StatusEvent> Queue { get; }

    /// <summary>The conversation's current run when the client subscribed.</summary>
    public string? RunId { get; internal init; }
    public bool Overflowed { get => Volatile.Read(ref _overflowed); internal set => Volatile.Write(ref _overflowed, value); }
    private bool _overflowed;

    /// <summary>The most events this client's queue ever held at once.</summary>
    public int PeakQueued => Volatile.Read(ref _peak);

    internal void NotePeak()
    {
        int now = Queue.Reader.Count;
        if (now > _peak) _peak = now;
    }

    public void Close() => _log.Unsubscribe(this, _conversation);
}
