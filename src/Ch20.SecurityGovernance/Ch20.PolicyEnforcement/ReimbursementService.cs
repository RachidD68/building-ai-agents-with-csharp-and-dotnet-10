using System.Collections.Concurrent;
using Ch08.ActionPolicy;

namespace Ch20.PolicyEnforcement;

/// <summary>What any transport hands the service: the model's proposal, nothing about who is asking.</summary>
public sealed record ReimbursementCommand(Guid OperationId, string EmployeeId, decimal Amount, string Currency);

public sealed record Outcome(Decision Decision, string Reason, decimal? BalanceAfter = null)
{
    public override string ToString() => $"{Decision} {Reason}";
}

public enum RuleMode { Observe, Enforce }

/// <summary>
/// Modes for the rules added after launch. A new rule starts in Observe: it
/// records what it would deny and lets the request through, so its effect can
/// be measured before it blocks anyone. The baseline rules (Ch08's policy:
/// permission, scope, amount, currency, version) have no mode and cannot be
/// switched off.
/// </summary>
public sealed class RuleModes
{
    public const string DailyLimit = "daily_limit";
    private readonly ConcurrentDictionary<string, RuleMode> _modes = new() { [DailyLimit] = RuleMode.Observe };

    public RuleMode this[string rule] => _modes[rule];

    public void Set(string rule, RuleMode mode)
    {
        if (!_modes.ContainsKey(rule))
            throw new ArgumentException($"'{rule}' is not an added rule; baseline rules are always enforced.", nameof(rule));
        _modes[rule] = mode;
    }
}

/// <summary>
/// Revoked tools and subjects, read at every decision. Nothing caches it at
/// session start, so a revocation reaches sessions that are already open.
/// A tool revocation is kept per source (security, an operations switch, ...):
/// a source lifts only its own, and the tool stays revoked while any is active.
/// </summary>
public sealed class RevocationList
{
    private readonly ConcurrentDictionary<(string Tool, string Source), string> _tools = new();
    private readonly ConcurrentDictionary<string, string> _subjects = new(StringComparer.Ordinal);

    public void RevokeTool(string tool, string reason) => RevokeTool(tool, "security", reason);
    public void RestoreTool(string tool) => RestoreTool(tool, "security");
    public void RevokeTool(string tool, string source, string reason) => _tools[(tool, source)] = reason;
    public void RestoreTool(string tool, string source) => _tools.TryRemove((tool, source), out _);
    public void RevokeSubject(string tenantId, string subjectId, string reason) => _subjects[$"{tenantId}/{subjectId}"] = reason;
    public void RestoreSubject(string tenantId, string subjectId) => _subjects.TryRemove($"{tenantId}/{subjectId}", out _);

    public bool IsToolRevoked(string tool) => _tools.Keys.Any(k => k.Tool == tool);
    public bool IsSubjectRevoked(Caller caller) => _subjects.ContainsKey($"{caller.TenantId}/{caller.SubjectId}");
}

/// <summary>One decision, with no request text: who, through what, about what, and why.</summary>
public sealed record AuditEntry(string Transport, string TenantId, string SubjectId, string EmployeeId,
    decimal Amount, Decision Decision, string Reason, IReadOnlyList<string> WouldDeny);

public sealed class AuditLog
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    public IReadOnlyList<AuditEntry> Entries => [.. _entries];
    public void Add(AuditEntry entry) => _entries.Enqueue(entry);
}

/// <summary>Accounts by employee id, across tenants, as a shared database would hold them.</summary>
public sealed class AccountStore
{
    internal sealed class Handle(EmployeeAccount account)
    {
        public readonly Lock Gate = new();
        public EmployeeAccount Current = account;
        public readonly HashSet<Guid> Operations = [];
        public readonly Dictionary<DateOnly, int> PaidPerDay = [];
    }

    private readonly ConcurrentDictionary<string, Handle> _accounts = new(StringComparer.Ordinal);

    public void Add(EmployeeAccount account) => _accounts[account.EmployeeId] = new Handle(account);
    public EmployeeAccount Get(string employeeId) => _accounts[employeeId].Current;
    internal bool TryGetHandle(string employeeId, out Handle handle) => _accounts.TryGetValue(employeeId, out handle!);

    /// <summary>For the first-edition reproductions only: a debit with no decision in front of it.</summary>
    internal void DebitUnchecked(string employeeId, decimal amount)
    {
        Handle h = _accounts[employeeId];
        lock (h.Gate) h.Current = h.Current with { ReimbursableBalance = h.Current.ReimbursableBalance - amount, Version = h.Current.Version + 1 };
    }
}

/// <summary>
/// The one decision point, at the effect. HTTP, MCP and the agent hosts
/// (A2A, AG-UI) all end here, with a caller each of them built from its own
/// authentication context. The decision and the debit happen under the
/// account's lock, so two concurrent claims cannot both spend the same balance.
/// </summary>
public sealed class ReimbursementService(
    AccountStore accounts, RevocationList revocations, RuleModes modes, AuditLog audit, TimeProvider clock)
{
    public const string Tool = "reimburse";
    public const int DailyLimitPerEmployee = 2;

    public Outcome Reimburse(string transport, Caller caller, ReimbursementCommand command)
    {
        List<string> wouldDeny = [];
        Outcome outcome = Decide(caller, command, wouldDeny);
        audit.Add(new AuditEntry(transport, caller.TenantId, caller.SubjectId, command.EmployeeId,
            command.Amount, outcome.Decision, outcome.Reason, wouldDeny));
        return outcome;
    }

    private Outcome Decide(Caller caller, ReimbursementCommand command, List<string> wouldDeny)
    {
        if (revocations.IsToolRevoked(Tool)) return Deny("tool_revoked");
        if (revocations.IsSubjectRevoked(caller)) return Deny("subject_revoked");
        DateTimeOffset now = clock.GetUtcNow();
        if (!accounts.TryGetHandle(command.EmployeeId, out AccountStore.Handle handle))
            return Unknown(caller, command, now);

        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        lock (handle.Gate)
        {
            EmployeeAccount account = handle.Current;
            ReimbursementProposal proposal = new(command.OperationId, command.EmployeeId,
                command.Amount, command.Currency, ExpectedVersion: account.Version);
            PolicyResult baseline = ReimbursementPolicy.Evaluate(caller, account, proposal, approval: null, now);
            if (baseline.Decision != Decision.Allow) return new(baseline.Decision, baseline.Reason);
            if (handle.Operations.Contains(command.OperationId)) return Deny("duplicate_operation");

            if (handle.PaidPerDay.GetValueOrDefault(today) >= DailyLimitPerEmployee)
            {
                if (modes[RuleModes.DailyLimit] == RuleMode.Enforce) return Deny(RuleModes.DailyLimit);
                wouldDeny.Add(RuleModes.DailyLimit);
            }

            handle.Operations.Add(command.OperationId);
            handle.PaidPerDay[today] = handle.PaidPerDay.GetValueOrDefault(today) + 1;
            handle.Current = account with
            {
                ReimbursableBalance = account.ReimbursableBalance - command.Amount,
                Version = account.Version + 1,
            };
            return new(Decision.Allow, baseline.Reason, handle.Current.ReimbursableBalance);
        }
    }

    // An unknown employee must answer exactly as another tenant's would, for
    // every caller, or the reason code tells a caller which ids exist. So the
    // same baseline runs against a placeholder whose tenant is blank: Ch08's
    // policy refuses a blank caller tenant, so no caller can ever match it.
    private static Outcome Unknown(Caller caller, ReimbursementCommand command, DateTimeOffset now)
    {
        EmployeeAccount nobody = new(TenantId: "", command.EmployeeId, Currency: "", ReimbursableBalance: 0m, Version: 0);
        PolicyResult baseline = ReimbursementPolicy.Evaluate(caller, nobody,
            new(command.OperationId, command.EmployeeId, command.Amount, command.Currency, ExpectedVersion: 0),
            approval: null, now);
        return baseline.Decision == Decision.Deny ? Deny(baseline.Reason) : Deny("resource_scope");
    }

    private static Outcome Deny(string reason) => new(Decision.Deny, reason);
}
