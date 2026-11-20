using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Ch23.QualificationRunner;

/// <summary>What a child process did: exit code, everything it printed, how long, and why it stopped.</summary>
public sealed record RawRun(int ExitCode, string Output, double Seconds, string? StoppedBy = null);

/// <summary>Runs one step. The real one starts dotnet; the self-test passes canned runs.</summary>
public delegate Task<RawRun> StepExecutor(StepSpec step, string[] command, TimeSpan timeout);

public sealed record StepResult(string Id, string Kind, string? Project, string Command, string Provider,
    int? ExitCode, int Passed, int Total, double Seconds, string Status, string? Reason, string? Log);

public sealed record LayerResult(string Id, string Title, string Mode, string Proves, string DoesNotProve,
    string Status, List<StepResult> Steps);

public static class StepStatus
{
    public const string Passed = "passed", Failed = "failed", Completed = "completed", Refused = "refused",
        NotRun = "not-run", Stopped = "stopped";
}

/// <summary>
/// Runs layers of steps and judges each one by its counts, not only its exit code:
/// a step that selected zero cases is refused, whatever it exited with.
/// </summary>
public sealed class Campaign(string logDir, StepExecutor execute, Func<IDisposable>? modelLock = null)
{
    public TimeSpan? Budget { get; init; }
    /// <summary>An in-process step (the model-identity probe); its output is judged like a child's.</summary>
    public Func<StepSpec, TimeSpan, Task<RawRun>>? Probe { get; init; }
    public TimeSpan Spent { get; private set; }
    public TimeSpan ModelLockHeld { get; private set; }

    public async Task<LayerResult> RunLayerAsync(LayerSpec layer, IReadOnlyList<string> identityProjects)
    {
        List<StepSpec> steps = layer.Generate == "restore-build"
            ? [.. identityProjects.SelectMany(p => new[]
              {
                  new StepSpec { Id = "restore:" + Short(p), Kind = "restore", Project = p },
                  new StepSpec { Id = "build:" + Short(p), Kind = "build", Project = p },
              })]
            : layer.Steps;
        List<StepResult> results = [];
        foreach (StepSpec step in steps) results.Add(await RunStepAsync(step));
        string status = results.Count == 0 ? StepStatus.Refused
            : results.Any(r => r.Status is StepStatus.Failed) ? StepStatus.Failed
            : results.All(r => r.Status is StepStatus.Passed) ? StepStatus.Passed
            : results.All(r => r.Status is StepStatus.Passed or StepStatus.Completed) ? StepStatus.Completed
            : "incomplete";
        return new(layer.Id, layer.Title, layer.Mode, layer.Proves, layer.DoesNotProve, status, results);
    }

    public async Task<StepResult> RunStepAsync(StepSpec step)
    {
        string[] command = Command(step);
        string shown = step.Kind == "probe" ? "in-process model identity probe" : "dotnet " + string.Join(' ', command);
        if (step.RequiresEnv.FirstOrDefault(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))) is { } missing)
            return new(step.Id, step.Kind, step.Project, shown, step.Provider, null, 0, 0, 0, StepStatus.NotRun, $"not configured: {missing} is not set", null);
        TimeSpan timeout = TimeSpan.FromSeconds(step.MaxSeconds);
        if (Budget is { } budget)
        {
            TimeSpan left = budget - Spent;
            if (left <= TimeSpan.Zero)
                return new(step.Id, step.Kind, step.Project, shown, step.Provider, null, 0, 0, 0, StepStatus.NotRun, "budget exhausted before the step", null);
            if (left < timeout) timeout = left;
        }

        RawRun run;
        bool usesModel = step.Provider is "ollama";
        Stopwatch held = Stopwatch.StartNew();
        using (usesModel && modelLock is not null ? modelLock() : null)
        {
            held.Restart();
            run = step.Kind == "probe" && Probe is not null ? await Probe(step, timeout) : await execute(step, command, timeout);
            if (usesModel) ModelLockHeld += held.Elapsed;
        }
        Spent += TimeSpan.FromSeconds(run.Seconds);

        string log = Path.Combine(logDir, Safe(step.Id) + ".log");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(log, $"$ {shown}\n{run.Output}\n# exit {run.ExitCode}, {run.Seconds:F1} s{(run.StoppedBy is null ? "" : ", stopped by " + run.StoppedBy)}\n", new UTF8Encoding(false));
        string relLog = Path.GetRelativePath(Path.GetDirectoryName(logDir)!, log).Replace('\\', '/');

        (int passed, int total, string status, string? reason) = Judge(step, run);
        return new(step.Id, step.Kind, step.Project, shown, step.Provider, run.ExitCode, passed, total,
            Math.Round(run.Seconds, 1), status, reason, relLog);
    }

    /// <summary>The rules. Exit code and counts must agree; zero cases is never green.</summary>
    public static (int Passed, int Total, string Status, string? Reason) Judge(StepSpec step, RawRun run)
    {
        if (run.StoppedBy is not null) return (0, 0, StepStatus.Stopped, $"stopped by {run.StoppedBy}");
        switch (step.Kind)
        {
            case "restore":
                return run.ExitCode == 0 ? (1, 1, StepStatus.Passed, null) : (0, 1, StepStatus.Failed, $"exit {run.ExitCode}");
            case "build":
                Match w = Regex.Match(run.Output, @"(\d+) Warning\(s\)"), e = Regex.Match(run.Output, @"(\d+) Error\(s\)");
                if (!w.Success || !e.Success) return (0, 0, StepStatus.Refused, "no build summary: nothing shows what was compiled");
                bool clean = run.ExitCode == 0 && w.Groups[1].Value == "0" && e.Groups[1].Value == "0";
                return (clean ? 1 : 0, 1, clean ? StepStatus.Passed : StepStatus.Failed,
                    clean ? null : $"exit {run.ExitCode}, {w.Groups[1].Value} warning(s), {e.Groups[1].Value} error(s)");
        }
        (int p, int t) = step.Count.Apply(run.Output);
        if (t == 0) return (0, 0, StepStatus.Refused, $"selected 0 cases (exit {run.ExitCode})");
        if (step.Provider is "ollama" or "azure")
            return step.CompletedExitCodes.Contains(run.ExitCode)
                ? (p, t, StepStatus.Completed, null)
                : (p, t, StepStatus.Failed, $"exit {run.ExitCode}: the run did not complete");
        if (run.ExitCode != 0) return (p, t, StepStatus.Failed, $"exit {run.ExitCode}");
        if (p != t) return (p, t, StepStatus.Failed, $"exit 0 but {t - p} of {t} failed");
        return (p, t, StepStatus.Passed, null);
    }

    public string[] Command(StepSpec step) => step.Kind switch
    {
        "restore" => ["restore", step.Project!, "--locked-mode"],
        "build" => ["build", step.Project!, "-c", "Release", "--no-restore", "-warnaserror", "-tl:off", "-nologo"],
        "run" => ["run", "--project", step.Project!, "-c", "Release", "--no-build", "--no-launch-profile", "--", .. step.Args],
        "probe" => ["(in-process probe)", .. step.Args],
        _ => throw new InvalidDataException($"step {step.Id}: unknown kind {step.Kind}"),
    };

    private static string Short(string project) => Path.GetFileNameWithoutExtension(project);

    private static string Safe(string id) => Regex.Replace(id, @"[^A-Za-z0-9._-]", "_");

    /// <summary>The real executor: dotnet as a child process in the repo root, killed on timeout.</summary>
    public static StepExecutor Dotnet(string repo) => async (step, command, timeout) =>
    {
        ProcessStartInfo psi = new("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, WorkingDirectory = repo,
        };
        foreach (string a in command) psi.ArgumentList.Add(a);
        foreach ((string k, string v) in step.Env) psi.Environment[k] = v;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        StringBuilder output = new();
        Stopwatch clock = Stopwatch.StartNew();
        using Process p = new() { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using CancellationTokenSource cts = new(timeout);
        string? stopped = null;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync();
            stopped = $"the {timeout.TotalSeconds:F0} s limit";
        }
        p.WaitForExit();
        lock (output) return new RawRun(p.ExitCode, output.ToString().TrimEnd(), clock.Elapsed.TotalSeconds, stopped);
    };
}

/// <summary>
/// One model run at a time on this machine: the lock is a file created exclusively.
/// Retries every 15 s while another run holds it, gives up after 45 min, deletes it when done.
/// </summary>
public sealed class ModelLock : IDisposable
{
    private readonly FileStream _file;
    private readonly string _path;

    private ModelLock(FileStream file, string path) { _file = file; _path = path; }

    public static ModelLock? TryAcquire(string path)
    {
        try
        {
            FileStream f = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            f.Write(Encoding.ASCII.GetBytes($"Ch23.QualificationRunner pid {Environment.ProcessId}\n"));
            f.Flush();
            return new ModelLock(f, path);
        }
        catch (IOException) { return null; }
    }

    public static ModelLock Acquire(string path, TimeSpan? retry = null, TimeSpan? giveUp = null)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            if (TryAcquire(path) is { } held) return held;
            if (waited.Elapsed > (giveUp ?? TimeSpan.FromMinutes(45)))
                throw new TimeoutException($"model lock {path} still held after {waited.Elapsed.TotalMinutes:F0} min");
            Console.WriteLine($"  (model lock held by another run; retrying in {(retry ?? TimeSpan.FromSeconds(15)).TotalSeconds:F0} s)");
            Thread.Sleep(retry ?? TimeSpan.FromSeconds(15));
        }
    }

    public void Dispose()
    {
        _file.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
