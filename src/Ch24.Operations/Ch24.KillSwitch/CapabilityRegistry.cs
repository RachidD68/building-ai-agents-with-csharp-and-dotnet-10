namespace Ch24.KillSwitch;

/// <summary>Someone acting on the registry. Only the on-call role may change it.</summary>
public sealed record Operator(string Id, bool OnCall);

public enum ChangeResult { Changed, AlreadyInState, Refused }

/// <summary>One audited attempt to change a capability, refused attempts included.</summary>
public sealed record CapabilityChange(
    long Seq, DateTimeOffset At, string Capability, string Action, string Actor, string Incident, string Detail);

/// <summary>A refused call: which capability, through which surface.</summary>
public sealed record Denial(DateTimeOffset At, string Capability, string Surface);

/// <summary>The capability states as they were at one moment, for the incident record.</summary>
public sealed record CapabilitySnapshot(DateTimeOffset At, long LastSeq, IReadOnlyDictionary<string, string> States);

/// <summary>
/// The switches, one per capability, read on every call. Nothing caches them
/// at session start, so a withdrawal reaches conversations that are already
/// open. An unregistered capability counts as withdrawn (fail closed).
/// Re-enabling needs the id of the evaluation run that cleared the candidate.
/// Every change is passed to the propagators (for example the decision point
/// at the effect), so a path that skips this registry still meets the switch.
/// They run inside the lock: run after it, a concurrent withdraw and enable
/// could reach the effect in the opposite order and leave it open.
/// The on-call flag comes from the caller here (in production, from the
/// identity provider's role claim), and the evaluation-run id is recorded,
/// not looked up in the evaluation store.
/// </summary>
public sealed class CapabilityRegistry(TimeProvider clock)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string?> _withdrawn = new(StringComparer.Ordinal);   // name -> reason, null = enabled
    private readonly List<CapabilityChange> _audit = [];
    private readonly List<Denial> _denials = [];
    private readonly List<Action<string, bool, string>> _propagators = [];

    public void Register(params string[] capabilities)
    {
        lock (_gate) foreach (string c in capabilities) _withdrawn.TryAdd(c, null);
    }

    /// <summary>Called with (capability, withdrawn, reason) after every change.</summary>
    public void Propagate(Action<string, bool, string> propagator)
    {
        lock (_gate) _propagators.Add(propagator);
    }

    public bool IsWithdrawn(string capability, out string reason)
    {
        lock (_gate)
        {
            if (!_withdrawn.TryGetValue(capability, out string? r)) { reason = "not registered"; return true; }
            reason = r ?? "";
            return r is not null;
        }
    }

    public ChangeResult Withdraw(string capability, Operator by, string incident, string reason) =>
        Change(capability, by, incident, withdraw: true, reason);

    public ChangeResult Enable(string capability, Operator by, string incident, string evaluationRun) =>
        string.IsNullOrWhiteSpace(evaluationRun)
            ? Refuse(capability, by, incident, "enable", "no evaluation run")
            : Change(capability, by, incident, withdraw: false, $"cleared by {evaluationRun}");

    private ChangeResult Change(string capability, Operator by, string incident, bool withdraw, string detail)
    {
        string action = withdraw ? "withdraw" : "enable";
        if (!by.OnCall) return Refuse(capability, by, incident, action, "not on call");
        if (string.IsNullOrWhiteSpace(incident)) return Refuse(capability, by, incident, action, "no incident id");
        lock (_gate)
        {
            if (!_withdrawn.TryGetValue(capability, out string? current))
                return RefuseLocked(capability, by, incident, action, "not registered");
            if ((current is not null) == withdraw) return ChangeResult.AlreadyInState;
            _withdrawn[capability] = withdraw ? detail : null;
            AddLocked(capability, action, by, incident, detail);
            // Under the lock, so the effect sees the changes in the registry's order.
            foreach (Action<string, bool, string> p in _propagators) p(capability, withdraw, $"{incident}: {detail}");
            return ChangeResult.Changed;
        }
    }

    private ChangeResult Refuse(string capability, Operator by, string incident, string action, string why)
    {
        lock (_gate) return RefuseLocked(capability, by, incident, action, why);
    }

    private ChangeResult RefuseLocked(string capability, Operator by, string incident, string action, string why)
    {
        AddLocked(capability, action + " refused", by, incident, why);
        return ChangeResult.Refused;
    }

    private void AddLocked(string capability, string action, Operator by, string incident, string detail) =>
        _audit.Add(new(_audit.Count + 1, clock.GetUtcNow(), capability, action, by.Id, incident, detail));

    public void RecordDenial(string capability, string surface)
    {
        lock (_gate) _denials.Add(new(clock.GetUtcNow(), capability, surface));
    }

    public IReadOnlyList<CapabilityChange> Audit { get { lock (_gate) return [.. _audit]; } }
    public IReadOnlyList<Denial> Denials { get { lock (_gate) return [.. _denials]; } }

    public CapabilitySnapshot Snapshot()
    {
        lock (_gate)
            return new(clock.GetUtcNow(), _audit.Count,
                _withdrawn.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value is null ? "enabled" : "withdrawn"));
    }
}
