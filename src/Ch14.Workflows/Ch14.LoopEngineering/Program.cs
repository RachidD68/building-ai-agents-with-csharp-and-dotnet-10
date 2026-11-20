// ============================================================================
// Loop Engineering: the agent as a control system
// ----------------------------------------------------------------------------
// A deterministic HARNESS wraps an LLM in a self-correcting loop that repairs a
// deliberately broken C# file. Five phases run every iteration:
//
//   Plan     — the harness hands the agent the current compiler errors.
//   Act      — the agent calls the write_file tool with a corrected file.
//   Observe  — the harness runs `dotnet build` (the VERIFIER) and parses the
//              output into STRUCTURED diagnostics (not raw logs).
//   Reason   — those structured errors, plus a compact summary of prior attempts
//   & Repair   (working memory), feed the next Plan.
//   Evaluate — stop on success (0 errors) or on a strict stopping condition:
//              max iterations, or no progress between rounds.
//
// The MODEL proposes one fix per turn; the HARNESS owns the control flow and the
// stopping conditions. That inversion — code drives the loop, the model fills a
// single step — is the heart of loop engineering. Cross-references: the evaluation chapter
// (evaluation is the verifier), the MCP chapter (serve tools over MCP), the security chapter
// (guardrails for unattended loops).
//
// NOTE: any model will BUILD this project; DRIVING the loop to a green build
// needs a capable coding model. The default is Ollama + llama3.2 (offline, per
// the repo convention); point appsettings.json at a stronger model — a hosted
// GPT/Claude, or a local qwen3-coder — for a crisper run.
//
// Offline check of the stopping rules and the diagnostics parser, no model and
// no SDK needed: dotnet run -- --self-test. The process exits 0 only on SUCCESS:
// a loop that stops on its cap, or on no progress, has not succeeded.
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

const int MaxIterations = 6;

if (args.Contains("--self-test")) return LoopSelfTest.Run();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("=== Loop Engineering — a self-correcting code loop ===\n");

// ── The LLM (Ollama/llama3.2 by default — see the header note). ─────────────
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);
using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

// ── The working directory: a throwaway target project with ONE broken file the
//    loop must repair. This is the whole sandbox the agent can touch. ─────────
string workDir = Path.Combine(Path.GetTempPath(), "loop-engineering-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(workDir);
string targetCsproj = Path.Combine(workDir, "target.csproj");
string targetSource = Path.Combine(workDir, "Program.cs");

File.WriteAllText(targetCsproj,
    "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
    "  <PropertyGroup>\n" +
    "    <OutputType>Exe</OutputType>\n" +
    "    <TargetFramework>net10.0</TargetFramework>\n" +
    "    <ImplicitUsings>enable</ImplicitUsings>\n" +
    "  </PropertyGroup>\n" +
    "</Project>\n");

// Two deliberate compile errors: a missing semicolon and a typo'd method name.
File.WriteAllText(targetSource,
    "int radius = 5;\n" +
    "double area = 3.14159 * radius * radius   // BUG 1: missing semicolon\n" +
    "Console.WriteLin($\"Area = {area:F2}\");     // BUG 2: WriteLin is not a method\n");

Console.WriteLine($"Target project : {workDir}");
Console.WriteLine("Seeded Program.cs with two deliberate compile errors.\n");

// ── Shared build state + the two tools the agent is given. The harness ALSO
//    runs the verifier itself after each turn — that call is authoritative. ───
BuildResult lastBuild = RunBuild();   // Observe #0 (also does the one-time restore)

[Description("Overwrite the target C# file (Program.cs) with a full, corrected version.")]
string WriteTargetFile(
    [Description("The COMPLETE corrected contents of Program.cs.")] string content)
{
    File.WriteAllText(targetSource, content);
    return $"Wrote {content.Length} characters to Program.cs.";
}

[Description("Compile the target project and return the current compiler errors.")]
string RunVerifier()
{
    lastBuild = RunBuild();
    return lastBuild.Errors.Count == 0
        ? "BUILD OK — 0 errors."
        : $"BUILD FAILED — {lastBuild.Errors.Count} error(s):\n{Format(lastBuild.Errors)}";
}

ChatClientAgent agent = new(chatClient, new ChatClientAgentOptions
{
    Name = "RepairAgent",
    ChatOptions = new ChatOptions
    {
        Instructions =
            "You repair C# compiler errors. You receive the current errors and the full " +
            "source of Program.cs. Rewrite the ENTIRE file correctly and call write_file " +
            "with the corrected contents, then call run_verifier to confirm. Change only " +
            "what the errors require. Respond with tool calls, not prose.",
        Tools =
        [
            AIFunctionFactory.Create(WriteTargetFile, name: "write_file"),
            AIFunctionFactory.Create(RunVerifier, name: "run_verifier"),
        ]
    }
});

// ── The loop. The harness owns it; the model fills one step per turn. ────────
List<string> workingMemory = [];   // compact attempt summaries — NOT raw logs
string outcome;
LoopOutcome result;
int iteration = 0;

while (true)
{
    // EVALUATE — success, or the exhaustion stopping condition
    result = LoopPolicy.BeforeTurn(lastBuild.Errors.Count, iteration, MaxIterations);
    if (result == LoopOutcome.Success)
    {
        outcome = $"SUCCESS — the target compiles cleanly after {iteration} repair iteration(s).";
        break;
    }
    if (result == LoopOutcome.CapReached)
    {
        outcome = $"ESCALATE — still {lastBuild.Errors.Count} error(s) after {MaxIterations} iterations. Hand off to a human.";
        break;
    }

    iteration++;
    int errorsBefore = lastBuild.Errors.Count;
    Console.WriteLine($"── Iteration {iteration} — Plan: {errorsBefore} error(s) to fix ──────────────");

    // PLAN + REASON/REPAIR — structured errors + curated working memory. A FRESH,
    // stateless turn each time: the harness decides what the model sees, so the
    // context never bloats with raw build logs.
    string currentSource = File.ReadAllText(targetSource);
    string prompt =
        $"Compiler errors in Program.cs:\n{Format(lastBuild.Errors)}\n\n" +
        $"Current Program.cs:\n{currentSource}\n\n" +
        (workingMemory.Count > 0 ? $"Prior attempts:\n{string.Join("\n", workingMemory)}\n\n" : "") +
        "Fix every error. Call write_file with the full corrected file, then run_verifier.";

    // ACT — the agent's write_file tool runs inside RunAsync.
    try
    {
        await agent.RunAsync(prompt, cancellationToken: cts.Token);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        // Ctrl+C means STOP the loop, not "log and try again" — swallowing
        // it here would defeat the CancelKeyPress wiring above.
        outcome = $"CANCELLED — stopped by the user on iteration {iteration}.";
        result = LoopOutcome.Cancelled;
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   (agent turn failed: {ex.Message})");
    }

    // OBSERVE — authoritative: the harness runs the verifier itself.
    lastBuild = RunBuild();
    int errorsAfter = lastBuild.Errors.Count;
    Console.WriteLine($"   Observe: {errorsBefore} → {errorsAfter} error(s).\n");
    workingMemory.Add(
        $"iter {iteration}: {errorsBefore} → {errorsAfter} errors" +
        (errorsAfter > 0 ? $" (top: {lastBuild.Errors[0].Code} {lastBuild.Errors[0].Message})" : ""));

    // EVALUATE — no-progress stopping condition. A stuck loop is a failure, not a
    // reason to retry forever.
    result = LoopPolicy.AfterTurn(errorsBefore, errorsAfter);
    if (result == LoopOutcome.NoProgress)
    {
        outcome = $"ESCALATE — no progress on iteration {iteration} ({errorsBefore} → {errorsAfter}). Stopping.";
        break;
    }
}

Console.WriteLine("───────────────────────────────────────────────────────────");
Console.WriteLine(outcome);
if (lastBuild.Errors.Count == 0)
    Console.WriteLine($"\nFinal Program.cs:\n{File.ReadAllText(targetSource)}");

Console.WriteLine(
    "\n[cost] Every iteration is a model call. Unattended loops spend real money and " +
    "time — the max-iteration and no-progress guards above are what keep a stuck loop " +
    "from becoming a runaway bill.");

try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort cleanup */ }
return LoopPolicy.ExitCode(result);   // only SUCCESS exits 0

// ── The VERIFIER: compile the target, parse diagnostics into structure. ──────
BuildResult RunBuild()
{
    ProcessStartInfo psi = new("dotnet", $"build \"{targetCsproj}\" --nologo -v quiet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = workDir,
    };
    using Process proc = Process.Start(psi)!;
    string output = proc.StandardOutput.ReadToEnd() + "\n" + proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return new BuildResult(proc.ExitCode, LoopPolicy.ParseErrors(output));
}

static string Format(List<Diag> errors)
{
    StringBuilder sb = new();
    foreach (Diag d in errors)
        sb.AppendLine($"  line {d.Line}: {d.Code}: {d.Message}");
    return sb.ToString().TrimEnd();
}

record Diag(int Line, string Code, string Message);
record BuildResult(int ExitCode, List<Diag> Errors);

enum LoopOutcome { Continue, Success, CapReached, NoProgress, Cancelled }

/// <summary>The harness's stopping rules and its verifier parser, kept pure so they can be tested.</summary>
static class LoopPolicy
{
    /// <summary>Before a turn: done when the build is clean, stop when the cap is used up.</summary>
    public static LoopOutcome BeforeTurn(int errors, int iteration, int maxIterations) =>
        errors == 0 ? LoopOutcome.Success
        : iteration >= maxIterations ? LoopOutcome.CapReached
        : LoopOutcome.Continue;

    /// <summary>After a turn: the same or more errors is no progress, so stop.</summary>
    public static LoopOutcome AfterTurn(int errorsBefore, int errorsAfter) =>
        errorsAfter >= errorsBefore ? LoopOutcome.NoProgress : LoopOutcome.Continue;

    /// <summary>A cap or a stall is not success.</summary>
    public static int ExitCode(LoopOutcome outcome) => outcome == LoopOutcome.Success ? 0 : 2;

    /// <summary>Compiler output to structured diagnostics, one per code and line.</summary>
    public static List<Diag> ParseErrors(string output)
    {
        List<Diag> errors = [];
        HashSet<string> seen = [];
        foreach (Match m in Regex.Matches(
            output, @"\((\d+),\d+\):\s*error\s+(CS\d+):\s*(.+?)(?:\s*\[|$)", RegexOptions.Multiline))
        {
            Diag d = new(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value.Trim());
            if (seen.Add($"{d.Code}@{d.Line}"))
                errors.Add(d);
        }
        return errors;
    }

    /// <summary>Drives the rules over a scripted verifier: the error count after each turn.</summary>
    public static (LoopOutcome Outcome, int Turns) Simulate(int initialErrors, IEnumerable<int> afterEachTurn, int maxIterations)
    {
        using IEnumerator<int> next = afterEachTurn.GetEnumerator();
        int errors = initialErrors;
        for (int turn = 0; ; turn++)
        {
            LoopOutcome before = BeforeTurn(errors, turn, maxIterations);
            if (before != LoopOutcome.Continue) return (before, turn);
            int after = next.MoveNext() ? next.Current : errors;
            if (AfterTurn(errors, after) == LoopOutcome.NoProgress) return (LoopOutcome.NoProgress, turn + 1);
            errors = after;
        }
    }
}

/// <summary>Offline checks: dotnet run -- --self-test</summary>
static class LoopSelfTest
{
    public static int Run()
    {
        Console.WriteLine("=== Loop Engineering self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        // A scripted verifier stands in for dotnet build; the model is not involved.
        Check("2 -> 1 -> 0 errors: SUCCESS after 2 turns, exit 0",
            LoopPolicy.Simulate(2, [1, 0], 6) is (LoopOutcome.Success, 2) && LoopPolicy.ExitCode(LoopOutcome.Success) == 0);
        Check("already clean: SUCCESS with no turn", LoopPolicy.Simulate(0, [], 6) is (LoopOutcome.Success, 0));
        Check("2 -> 2 errors: NO PROGRESS after 1 turn, exit 2",
            LoopPolicy.Simulate(2, [2], 6) is (LoopOutcome.NoProgress, 1) && LoopPolicy.ExitCode(LoopOutcome.NoProgress) == 2);
        Check("2 -> 3 errors (worse): NO PROGRESS", LoopPolicy.Simulate(2, [3], 6) is (LoopOutcome.NoProgress, 1));
        Check("9 -> 8 -> 7 -> 6 with cap 3: CAP REACHED with 6 errors left, exit 2 (cap is not success)",
            LoopPolicy.Simulate(9, [8, 7, 6], 3) is (LoopOutcome.CapReached, 3) && LoopPolicy.ExitCode(LoopOutcome.CapReached) == 2);
        Check("cancelled is not success", LoopPolicy.ExitCode(LoopOutcome.Cancelled) != 0);

        // A canned log in the compiler's format; MSBuild repeats an error, the parser keeps one.
        const string log =
            "Program.cs(2,42): error CS1002: ; expected [target.csproj]\n" +
            "Program.cs(3,9): error CS0117: 'Console' does not contain a definition for 'WriteLin' [target.csproj]\n" +
            "Program.cs(3,9): error CS0117: 'Console' does not contain a definition for 'WriteLin' [target.csproj]\n" +
            "Build FAILED.\n";
        List<Diag> diags = LoopPolicy.ParseErrors(log);
        Check("verifier parser: 2 structured errors, duplicate dropped, project suffix stripped",
            diags.Count == 2 && diags[0] == new Diag(2, "CS1002", "; expected") && diags[1].Code == "CS0117" &&
            diags[1].Line == 3 && !diags[1].Message.Contains('['));
        Check("verifier parser: a clean log has no errors", LoopPolicy.ParseErrors("Build succeeded.\n    0 Error(s)\n").Count == 0);

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }
}
