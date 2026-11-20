using Microsoft.Agents.AI;

namespace Ch16.SkillLifecycle;

/// <summary>One line of the run log: which run, which skill, what happened, with which content hash.</summary>
public sealed record SkillEvent(string Run, string Skill, string Operation, string Sha256, string Detail = "");

/// <summary>
/// The lifecycle gate. It passes on only skills whose current content hash matches an
/// approved, non-withdrawn record, and pins each one to that hash. The provider asks
/// its source again before every model call and every skill tool call, so the pin is
/// held per run: open one with BeginRun around RunAsync, and every call in that run
/// sees the same pinned skills. Outside a run scope every call is evaluated on its own.
/// </summary>
public sealed class GatedSkillsSource(AgentSkillsSource inner, SkillRegistry registry) : DelegatingAgentSkillsSource(inner)
{
    private readonly AsyncLocal<RunPin?> _currentRun = new();   // per gate: two gates in one flow keep their own pins
    private readonly List<SkillEvent> _log = [];
    private readonly Lock _gate = new();
    private int _calls;

    private sealed class RunPin(string id)
    {
        public string Id { get; } = id;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IList<AgentSkill>? Skills { get; set; }
    }

    private sealed class Scope(Action end) : IDisposable { public void Dispose() => end(); }

    public IReadOnlyList<SkillEvent> Log { get { lock (_gate) return [.. _log]; } }

    internal void Record(SkillEvent e) { lock (_gate) _log.Add(e); }

    /// <summary>Starts one run (an operation): the first skill lookup in it pins, later ones reuse the pin.</summary>
    public IDisposable BeginRun(string runId)
    {
        RunPin? previous = _currentRun.Value;
        _currentRun.Value = new RunPin(runId);
        return new Scope(() => _currentRun.Value = previous);
    }

    public override async Task<IList<AgentSkill>> GetSkillsAsync(AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
    {
        if (_currentRun.Value is not { } pin)
        {
            return await EvaluateAsync($"call-{Interlocked.Increment(ref _calls)}", context, cancellationToken);
        }
        await pin.Gate.WaitAsync(cancellationToken);
        try
        {
            return pin.Skills ??= await EvaluateAsync(pin.Id, context, cancellationToken);
        }
        finally
        {
            pin.Gate.Release();
        }
    }

    private async Task<IList<AgentSkill>> EvaluateAsync(string run, AgentSkillsSourceContext context, CancellationToken cancellationToken)
    {
        List<AgentSkill> active = [];
        foreach (AgentSkill skill in await base.GetSkillsAsync(context, cancellationToken))
        {
            string name = skill.Frontmatter.Name;
            SkillSnapshot snapshot = await SkillRegistry.SnapshotAsync(skill, cancellationToken);
            string hash = snapshot.Sha256;
            SkillRecord? approved = registry.Find(name);
            string? refusal = approved switch
            {
                null => "not validated",
                { Withdrawn: true } => $"withdrawn: {approved.WithdrawnReason}",
                _ when approved.Sha256 != hash => $"hash mismatch, approved {approved.Sha256[..12]}",
                _ => null,
            };
            if (refusal is not null)
            {
                Record(new(run, name, "refused", hash, refusal));
                continue;
            }
            // Snapshot the body now: an edit later in this run cannot change what it loads.
            string body = await skill.GetContentAsync(cancellationToken);
            Record(new(run, name, "activated", hash));
            active.Add(new PinnedSkill(skill, body, snapshot, run, registry, this));
        }
        return active;
    }
}

/// <summary>A skill pinned to one run and one content hash. Every operation is logged with that hash.</summary>
internal sealed class PinnedSkill(AgentSkill inner, string body, SkillSnapshot snapshot, string run,
    SkillRegistry registry, GatedSkillsSource source) : AgentSkill
{
    private string Hash => snapshot.Sha256;
    private SkillSnapshot Pinned => snapshot;

    public override AgentSkillFrontmatter Frontmatter => inner.Frontmatter;

    public override ValueTask<string> GetContentAsync(CancellationToken cancellationToken = default)
    {
        Guard("load");
        return ValueTask.FromResult(body);
    }

    public override async ValueTask<AgentSkillResource?> GetResourceAsync(string name, CancellationToken cancellationToken = default)
    {
        await GuardUnchangedAsync($"read {name}", cancellationToken);
        AgentSkillResource? resource = await inner.GetResourceAsync(name, cancellationToken);
        // The provider reads a file resource later. Serve the bytes the pin checked, not whatever is on disk by then.
        return resource is not null && inner is AgentFileSkill folder
            ? new PinnedFileResource(resource, Path.Combine(folder.Path, name), name.Replace('\\', '/'), this)
            : resource;
    }

    public override async ValueTask<AgentSkillScript?> GetScriptAsync(string name, CancellationToken cancellationToken = default)
    {
        await GuardUnchangedAsync($"script {name}", cancellationToken);
        return await inner.GetScriptAsync(name, cancellationToken);
    }

    // Withdrawal is checked on every operation, so it reaches runs already in flight.
    private void Guard(string operation)
    {
        SkillRecord? record = registry.Find(Frontmatter.Name);
        if (record is null || record.Withdrawn)
        {
            source.Record(new(run, Frontmatter.Name, $"{operation} refused", Hash, "withdrawn"));
            throw new InvalidOperationException($"Skill '{Frontmatter.Name}' has been withdrawn.");
        }
        source.Record(new(run, Frontmatter.Name, operation, Hash));
    }

    // Resources and scripts are read from disk, so check the folder still hashes as pinned.
    private async ValueTask GuardUnchangedAsync(string operation, CancellationToken ct)
    {
        string now = await SkillRegistry.HashAsync(inner, ct);
        if (now != Hash) Refuse(operation, now);
        Guard(operation);
    }

    private void Refuse(string operation, string now)
    {
        source.Record(new(run, Frontmatter.Name, $"{operation} refused", now, "changed during the run"));
        throw new InvalidOperationException($"Skill '{Frontmatter.Name}' changed during the run; revalidate it.");
    }

    /// <summary>Reads the file itself, compares its bytes with the activation snapshot, and serves exactly those bytes.</summary>
    private sealed class PinnedFileResource(AgentSkillResource file, string fullPath, string relativePath, PinnedSkill owner)
        : AgentSkillResource(file.Name, file.Description)
    {
        public override async Task<object?> ReadAsync(IServiceProvider? serviceProvider = null, CancellationToken cancellationToken = default)
        {
            byte[] raw = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            string now = SkillRegistry.FileHash(raw);
            if (!owner.Pinned.Files.TryGetValue(relativePath, out string? pinned) || pinned != now)
            {
                owner.Refuse($"read {relativePath}", owner.Hash);
            }
            return SkillRegistry.DecodeText(raw);
        }
    }
}
