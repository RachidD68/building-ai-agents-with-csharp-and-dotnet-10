// ============================================================================
// Qualification runner: freeze a candidate, run the campaign in layers, write
// the evidence. Every number the chapter prints comes from the file this
// writes, qualification-results.json.
// ----------------------------------------------------------------------------
//   dotnet run -- --self-test
//       the runner's own rules on canned runs and temp folders, offline. RESULT n/n
//   dotnet run -- --offline [--repo <dir>] [--manifest <file>] [--out <dir>]
//       secret scan of the manifest, candidate identity, then the offline layers:
//       restore --locked-mode and -warnaserror build of every candidate project,
//       every --self-test of the candidate with its counts and exit codes
//   dotnet run -- --real --budget-minutes <n> --into <results.json> [--model-lock <file>]
//       the real-provider and workload layers, added to an offline run of the
//       SAME candidate (identity re-computed and compared), under a time budget;
//       each model step holds the machine's model lock while it runs
//   dotnet run -- --dossier <results.json> [--journal <file>] [--capabilities <file>]
//       release-dossier.md from the results, the failure journal, the capabilities
// Default manifest: contoso/v7-qualified/release/candidate.json.
// Default output: <temp>/AgenticAI/Ch23.Qualification.
// ============================================================================

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Ch23.QualificationRunner;

if (args.Contains("--self-test")) return await RunnerSelfTest.RunAsync();

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

string repo = Path.GetFullPath(Opt("--repo") ?? FindRepo(Directory.GetCurrentDirectory()) ?? FindRepo(AppContext.BaseDirectory)
    ?? throw new DirectoryNotFoundException("AgenticAI.slnx not found above this folder; pass --repo"));
string manifestPath = Path.GetFullPath(Opt("--manifest") ?? Path.Combine(repo, "contoso", "v7-qualified", "release", "candidate.json"));
string outDir = Path.GetFullPath(Opt("--out") ?? Path.Combine(Path.GetTempPath(), "AgenticAI", "Ch23.Qualification"));

if (Opt("--dossier") is { } resultsFile)
{
    QualificationResults r = Dossier.Load<QualificationResults>(resultsFile);
    string release = Path.GetDirectoryName(manifestPath)!;
    FailureJournal journal = Dossier.Load<FailureJournal>(Opt("--journal") ?? Path.Combine(release, "failure-journal.json"));
    CapabilityFile caps = Dossier.Load<CapabilityFile>(Opt("--capabilities") ?? Path.Combine(release, "capabilities.json"));
    CandidateManifest dm = CandidateManifest.Parse(File.ReadAllText(manifestPath));
    try
    {
        string md = Dossier.Render(r, journal, caps, dm.Limits);
        string target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultsFile))!, "release-dossier.md");
        File.WriteAllText(target, md, new UTF8Encoding(false));
        Console.WriteLine($"Dossier: {target}");
        return 0;
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

bool offline = args.Contains("--offline"), real = args.Contains("--real");
if (offline == real)
{
    Console.Error.WriteLine("Usage: --self-test | --offline | --real --budget-minutes <n> --into <results.json> | --dossier <results.json>");
    return 2;
}

Console.WriteLine("=== Qualification campaign ===\n");
string manifestJson = File.ReadAllText(manifestPath);
List<string> secrets = SecretScan.Find(manifestJson);
if (secrets.Count > 0)
{
    Console.Error.WriteLine("REFUSED: the manifest carries secret-like values:\n  " + string.Join("\n  ", secrets));
    return 2;
}
CandidateManifest m = CandidateManifest.Parse(manifestJson);
Console.WriteLine($"candidate {m.Candidate}; manifest {Path.GetRelativePath(repo, manifestPath)}; no secret-like value");
List<string> projects = CandidateIdentity.Closure(repo, m.Identity.Projects);
Directory.CreateDirectory(outDir);
string logs = Path.Combine(outDir, "logs");
string started = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
string sdkUsed = (await Campaign.Dotnet(repo)(new StepSpec { Id = "sdk", Kind = "info" }, ["--version"], TimeSpan.FromSeconds(60))).Output.Trim();

if (offline)
{
    CandidateIdentity before = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: false);
    if (before.Problems.Count > 0) return Refuse("the candidate is not frozen", before.Problems);
    Console.WriteLine($"{projects.Count} projects in the closure of {m.Identity.Projects.Count} roots; {before.Packages.Count} direct packages; {before.LockFiles.Count} lock files\n");

    Campaign campaign = new(logs, Campaign.Dotnet(repo));
    List<LayerResult> layers = [];
    CandidateIdentity? frozen = null;
    foreach (LayerSpec layer in m.Layers.Where(l => l.Mode == "offline"))
    {
        Console.WriteLine($"-- {layer.Title}");
        LayerResult lr = await campaign.RunLayerAsync(layer, m.Identity.Projects);
        Print(lr);
        layers.Add(lr);
        if (layer.Generate == "restore-build")
        {
            if (lr.Status != StepStatus.Passed) { Console.WriteLine("  the build layer did not pass: later layers are not run"); break; }
            frozen = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: true);
            if (frozen.Problems.Count > 0) return Refuse("the built candidate is incomplete", frozen.Problems);
            Console.WriteLine($"  frozen: identity {frozen.Sha256[..16]}, prompts {string.Join(", ", frozen.Prompts.Select(p => $"{p.Id} {p.Sha256[..8]}"))}");
        }
    }
    frozen ??= before;
    CandidateIdentity after = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: frozen != before);
    QualificationResults results = Assemble(["offline"], started, sdkUsed, frozen, after.Sha256 == frozen.Sha256, null, layers, m.Matrix,
        [new BudgetUse(null, Math.Round(campaign.Spent.TotalSeconds, 1), 0)]);
    return Write(results, layers.All(l => l.Status == StepStatus.Passed) && results.IdentityUnchangedAfterRun ? 0 : 1);
}

// --real
if (!double.TryParse(Opt("--budget-minutes"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double minutes) || minutes <= 0)
{
    Console.Error.WriteLine("REFUSED: --real needs an explicit --budget-minutes <n> (a positive number)");
    return 2;
}
if (Opt("--into") is not { } intoPath || !File.Exists(intoPath))
{
    Console.Error.WriteLine("REFUSED: --real adds to an offline run of the same candidate: --into <qualification-results.json>");
    return 2;
}
QualificationResults into = Dossier.Load<QualificationResults>(intoPath);
CandidateIdentity now = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: true);
if (now.Sha256 != into.CandidateSha256)
    return Refuse("the candidate changed since the offline layers", [$"offline run {into.CandidateSha256[..16]}, now {now.Sha256[..16]}", .. now.Problems]);
string lockPath = Opt("--model-lock") ?? Path.Combine(Path.GetTempPath(), "ollama.lock");
Console.WriteLine($"same candidate as the offline run ({now.Sha256[..16]}); budget {minutes} min; model lock {lockPath}\n");
Campaign realCampaign = new(logs, Campaign.Dotnet(repo), () => ModelLock.Acquire(lockPath))
{
    Budget = TimeSpan.FromMinutes(minutes),
    Probe = (step, timeout) => ModelProbe.RunAsync(m.Profile, timeout),
};
List<LayerResult> realLayers = [];
foreach (LayerSpec layer in m.Layers.Where(l => l.Mode == "real"))
{
    Console.WriteLine($"-- {layer.Title}");
    LayerResult lr = await realCampaign.RunLayerAsync(layer, m.Identity.Projects);
    Print(lr);
    realLayers.Add(lr);
}
StepResult? probe = realLayers.SelectMany(l => l.Steps).FirstOrDefault(s => s.Kind == "probe");
ObservedModel? observed = ModelProbe.Read(probe?.Log is null ? null : Path.Combine(outDir, probe.Log), m.Profile.ChatModel);
CandidateIdentity end = await CandidateIdentity.ComputeAsync(repo, manifestPath, m, promptsBuilt: true);
List<LayerResult> all = [.. into.Layers.Where(l => l.Mode != "real"), .. realLayers];
QualificationResults merged = Assemble([.. into.Modes.Where(x => x != "real"), "real"], into.StartedUtc, sdkUsed, into.Candidate,
    into.IdentityUnchangedAfterRun && end.Sha256 == into.CandidateSha256, observed, all, m.Matrix,
    [.. into.Budgets.Take(1), new BudgetUse(minutes, Math.Round(realCampaign.Spent.TotalSeconds, 1), Math.Round(realCampaign.ModelLockHeld.TotalSeconds, 1))]);
return Write(merged, realLayers.All(l => l.Status is StepStatus.Passed or StepStatus.Completed) ? 0 : 1);

// ---------------------------------------------------------------------------

QualificationResults Assemble(List<string> modes, string start, string sdk, CandidateIdentity id, bool unchanged, ObservedModel? model,
    List<LayerResult> layers, List<MatrixRow> rows, List<BudgetUse> budgets)
{
    Dictionary<string, StepResult> steps = layers.SelectMany(l => l.Steps).ToDictionary(s => s.Id);
    List<RowResult> matrix = [.. rows.Select(r => Matrix.Evaluate(r, steps, outDir))];
    return new QualificationResults
    {
        Modes = modes, StartedUtc = start, FinishedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        Platform = $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})", SdkUsed = sdk,
        CandidateSha256 = id.Sha256, Candidate = id, IdentityUnchangedAfterRun = unchanged, Model = model,
        Layers = layers, Matrix = matrix, Totals = Matrix.Count(layers, matrix), Budgets = budgets,
    };
}

int Write(QualificationResults r, int code)
{
    string file = Path.Combine(outDir, "qualification-results.json");
    File.WriteAllText(file, JsonSerializer.Serialize(r, CandidateManifest.Json), new UTF8Encoding(false));
    Totals t = r.Totals;
    Console.WriteLine($"\nidentity {r.CandidateSha256[..16]}; unchanged after the run: {(r.IdentityUnchangedAfterRun ? "yes" : "NO")}");
    Console.WriteLine($"steps {t.Steps}: {t.StepsPassed} passed, {t.StepsCompleted} completed (provider), {t.StepsFailed} failed, {t.StepsRefused} refused, {t.StepsNotRun} not run");
    Console.WriteLine($"cases {t.CasesPassed}/{t.Cases}");
    Console.WriteLine($"matrix {t.Rows} rows: {t.RowsPassed} passed, {t.RowsFailed} failed, {t.RowsNotRun} not run, {t.RowsRefused} refused, {t.RowsPendingHuman} pending human acceptance");
    foreach (RowResult row in r.Matrix.Where(x => x.Status != Matrix.Passed)) Console.WriteLine($"  {row.Id,-5} {row.Status,-13} {row.Observed}");
    Console.WriteLine($"results: {file}");
    return code;
}

int Refuse(string why, IEnumerable<string> problems)
{
    Console.Error.WriteLine($"REFUSED: {why}:\n  " + string.Join("\n  ", problems));
    return 2;
}

static void Print(LayerResult l)
{
    foreach (StepResult s in l.Steps)
        Console.WriteLine($"  {s.Status,-9} {s.Id,-40} {(s.Total > 0 ? $"{s.Passed}/{s.Total}" : "-"),-7} exit {(s.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"),-3} {s.Seconds,6:F1} s{(s.Reason is null ? "" : "  " + s.Reason)}");
    Console.WriteLine($"  layer: {l.Status}\n");
}

static string? FindRepo(string start)
{
    for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "AgenticAI.slnx"))) return d.FullName;
    return null;
}
