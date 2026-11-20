# Qualifying the Contoso agent system

| Project | What it shows |
|---|---|
| `Ch23.QualificationRunner` | Freeze a candidate from the repo (SDK request, package pins checked against every lock file, compiled prompts read from metadata, skill content hashes against their lock, data hashes), run the campaign in layers as child processes, refuse a step that selected zero cases, judge each acceptance-matrix row on the log of the test it names, and write `qualification-results.json`; render the release dossier from it |

```bash
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --self-test      # RESULT n/n, offline
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --offline --out <dir>
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --real --budget-minutes 20 --into <dir>/qualification-results.json --out <dir>
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --dossier <dir>/qualification-results.json
```

Rules the runner enforces:

- A step counts its cases from its output (`RESULT a/b` lines, a ratio or a
  tally, declared per step in the manifest). Zero cases is refused, whatever
  the exit code. Exit 0 with a failed case, or a non-zero exit with n/n, fails.
- A build without a warning and error summary is refused.
- A lock file that resolves a version other than `Directory.Packages.props`
  pins, a skill whose content differs from its lock, or a missing data version
  refuses the campaign before it starts.
- A provider or workload row of the matrix is never satisfied by a step that
  ran with a fake model or none. Human rows stay pending.
- `--real` needs `--budget-minutes` and the results of an offline run of the
  same candidate (`--into`); the identity is recomputed and compared. Each model
  step holds the machine's model lock (`--model-lock`, default `<temp>/ollama.lock`)
  only while it runs. A cloud step whose endpoint is not configured is not run,
  never counted.
- The manifest is refused if it holds a value under a secret-like name or a
  value that looks like a key or a signed URL.

The candidate is `contoso/v7-qualified` (see its README).
