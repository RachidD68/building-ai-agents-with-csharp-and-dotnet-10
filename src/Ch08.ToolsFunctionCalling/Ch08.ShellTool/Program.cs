// ============================================================================
// Chapter 8 — Building a shell tool (the most dangerous tool you'll build)
// ----------------------------------------------------------------------------
// A shell tool is an ordinary AIFunction — AIFunctionFactory.Create wrapping a
// delegate — whose body starts a process and returns its stdout. There is no
// special framework surface for this; that is the point. Shell tools are
// powerful — they let an agent inspect the filesystem, run scripts, query
// system state — and dangerous — the same delegate gives an attacker the keys
// to the host if you misconfigure it. The safety pattern in this sample:
//
//   1. Allow-list read-only viewers: `ls`, `dir`, `cat`, `type`, `wc`. `dotnet`
//      is allowed only for --version/--info/--list-sdks/--list-runtimes:
//      `dotnet run` or `dotnet exec` would run arbitrary code, and `find`
//      (dropped) can execute commands with -exec.
//   2. Confine every path argument to a scratch folder: rooted paths, `..`
//      escapes, UNC and drive-relative paths are refused, and flags must be
//      plain letters (`-l`, `-la`), so a path cannot hide inside a flag.
//   3. Take structured arguments: the tool accepts an executable name plus a
//      string[] of arguments and hands them to ProcessStartInfo.ArgumentList,
//      so each argument is passed verbatim to the program. There is no shell
//      to interpret metacharacters and no fragile Split(' ') that would mangle
//      a quoted path such as `cat "my notes.txt"`.
//   4. Cap execution time: 5 seconds per call.
//   5. Cap output size: 4 KB per call (truncate the rest).
//
// The real guards are #1, #2, and the combination of UseShellExecute = false +
// ArgumentList in #3 — together they mean the model can only launch a
// read-only program, on files inside the scratch folder, with arguments that
// never reach a shell. This is NOT a sandbox: the program still runs with your
// account's rights. Real isolation needs an OS boundary (a container or VM).
//
// Offline check of the policy: dotnet run -- --self-test
//
// In production, a shell tool should NEVER be exposed on a public endpoint
// without the safety pipeline from Chapter 16 (input sanitisation + critic
// agent + middleware). The example below is the "training-wheels" version; the
// productionized path is the preview Microsoft.Agents.AI.Tools.Shell package's
// LocalShellExecutor (or DockerShellExecutor for container isolation).
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticAI.Common.Extensions;

if (args.Contains("--self-test")) return ShellPolicy.SelfTest();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── The policy every call goes through (ShellPolicy, at the bottom) ──
// Deliberately ABSENT from the allow-list: pwsh, powershell, bash, sh, cmd.
// A shell or script host defeats every other guard in this file — the model
// could pass `-Command "Remove-Item ..."`. If you ever think you need one,
// what you actually need is a purpose-built tool for that one operation.
string scratchDir = Path.Combine(
    Path.GetTempPath(),
    $"ch08-shelltool-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratchDir);

// Seed the scratch folder with a couple of files for the agent to discover.
File.WriteAllText(Path.Combine(scratchDir, "README.md"),
    "# Scratch folder\nThis directory was created for the Ch08 shell-tool demo.\n");
File.WriteAllText(Path.Combine(scratchDir, "data.txt"),
    string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}")));

Console.WriteLine($"Scratch folder: {scratchDir}");
Console.WriteLine();

// ── The shell tool: an AIFunction the model can call ───────────────
// The signature is deliberately structured: an executable name plus a
// separate array of arguments. The model fills both, and we pass the
// arguments straight to ProcessStartInfo.ArgumentList — never to a shell,
// never re-parsed. That removes a whole class of quoting and injection bugs.
[Description("""
    Runs one read-only program in the scratch folder and returns its stdout.
    Allowed: ls, dir, cat, type, wc on files inside the scratch folder, and
    dotnet --version / --info / --list-sdks / --list-runtimes. Arguments are
    passed verbatim to the program — no shell is involved, so pipelines,
    redirects, and command chaining are impossible. Output is truncated at 4 KB.
""")]
async Task<string> RunShellAsync(
    [Description("The program to run, e.g. \"dotnet\", \"ls\", or \"cat\". Must be on the allow-list.")]
    string executable,
    [Description("The arguments to pass to the program, each as a separate array element, e.g. [\"--version\"] or [\"-la\"] or [\"README.md\"]. Pass an empty array for no arguments.")]
    string[]? arguments)
{
    // The policy is the gate: a refused call never reaches a process. Because
    // we never launch a shell (UseShellExecute = false below) and pass
    // arguments through ArgumentList, they cannot smuggle in a second command.
    string? refusal = ShellPolicy.Check(executable, arguments ?? [], scratchDir);
    if (refusal is not null)
    {
        return $"Error: {refusal}";
    }

    ProcessStartInfo startInfo = new()
    {
        FileName = executable,
        WorkingDirectory = scratchDir,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    // Add each argument individually. ArgumentList quotes and escapes each
    // element for us, so `cat "my notes.txt"` arrives as one argument — the
    // bug that command.Split(' ') would have introduced never happens.
    foreach (string arg in arguments ?? Array.Empty<string>())
    {
        startInfo.ArgumentList.Add(arg);
    }

    using Process proc = new() { StartInfo = startInfo };

    StringBuilder stdout = new();
    StringBuilder stderr = new();
    proc.OutputDataReceived += (_, e) =>
    {
        if (e.Data is not null && stdout.Length < 4096) stdout.AppendLine(e.Data);
    };
    proc.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is not null && stderr.Length < 4096) stderr.AppendLine(e.Data);
    };

    try
    {
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using CancellationTokenSource toolCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        toolCts.CancelAfter(TimeSpan.FromSeconds(5));
        await proc.WaitForExitAsync(toolCts.Token);
    }
    catch (OperationCanceledException)
    {
        try { proc.Kill(); } catch { }
        return "Error: command timed out after 5 seconds.";
    }

    string result = stdout.ToString();
    if (result.Length > 4096) result = result[..4096] + "\n[truncated at 4 KB]";
    if (proc.ExitCode != 0 && stderr.Length > 0)
    {
        result += $"\n[stderr] {stderr}";
    }
    return result.Trim();
}

AIFunction shellTool = AIFunctionFactory.Create(
    (Delegate)RunShellAsync,
    name: "run_shell",
    description: "Run a read-only, allow-listed program (with structured arguments) in the scratch folder.");

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a careful systems-engineer assistant with access to a shell
        tool. The tool runs a single read-only program in a scratch
        folder; call it with the program name and an array of arguments
        (for example, executable "ls" with arguments ["-la"]). Use it to
        answer questions about the directory's contents. Prefer fewer, more
        targeted commands over many small ones. Cite the actual output you
        observe.
        """,
    name: "ShellAssistant",
    tools: [shellTool]);

AgentSession session = await agent.CreateSessionAsync(cts.Token);

Console.WriteLine("=== Chapter 8: dotnet shell tool ===");
Console.WriteLine();

string task = """
    List the files in this directory. For each file, report its size in lines.
    Then tell me which version of dotnet is installed. Be concise.
""";

Console.WriteLine($"Task: {task}");
Console.WriteLine();
Console.WriteLine("Agent:");

await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
    task, session, cancellationToken: cts.Token))
{
    if (update.Text is { Length: > 0 } text)
    {
        Console.Write(text);
    }
}
Console.WriteLine();
Console.WriteLine();
Console.WriteLine("=== Done ===");

// Cleanup
try { Directory.Delete(scratchDir, recursive: true); } catch { }
return 0;

// ── The policy ─────────────────────────────────────────────────────
internal static class ShellPolicy
{
    static readonly HashSet<string> FileViewers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "dir", "cat", "type", "wc",
    };

    static readonly HashSet<string> DotnetInfoFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--version", "--info", "--list-sdks", "--list-runtimes",
    };

    /// <summary>Returns null when the call is allowed, otherwise why it is refused.</summary>
    public static string? Check(string? executable, IReadOnlyList<string> arguments, string scratchDir)
    {
        if (string.IsNullOrWhiteSpace(executable)) return "no executable specified.";

        if (executable.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.Count == 1 && DotnetInfoFlags.Contains(arguments[0])
                ? null
                : "dotnet is allowed only with --version, --info, --list-sdks or --list-runtimes.";
        }

        if (!FileViewers.Contains(executable))
            return $"program '{executable}' is not on the allow-list.";

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scratchDir)) + Path.DirectorySeparatorChar;
        foreach (string arg in arguments)
        {
            if (arg.StartsWith('-'))
            {
                // Flags must be plain letters (-l, -la, --all): no '=', no path inside a flag.
                if (!System.Text.RegularExpressions.Regex.IsMatch(arg, "^--?[A-Za-z]+$"))
                    return $"flag '{arg}' is not allowed.";
                continue;
            }
            // Rooted (C:\x, \x, /x), UNC (\\server\share) and drive-relative (D:x) paths are refused.
            if (Path.IsPathRooted(arg) || arg.Contains(':'))
                return $"'{arg}' is outside the scratch folder.";
            string full = Path.GetFullPath(Path.Combine(root, arg));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return $"'{arg}' is outside the scratch folder.";
        }
        return null;
    }

    /// <summary>Offline check: dotnet run -- --self-test</summary>
    public static int SelfTest()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "ch08-policy-selftest");
        (string Exe, string[] Args, bool Allowed)[] cases =
        [
            ("ls", [], true),
            ("ls", ["-la"], true),
            ("wc", ["-l", "data.txt"], true),
            ("cat", ["README.md"], true),
            ("cat", ["notes/today.txt"], true),
            ("dotnet", ["--version"], true),
            ("cat", [@"C:\Windows\win.ini"], false),
            ("cat", ["/etc/passwd"], false),
            ("cat", [@"..\..\secrets.txt"], false),
            ("cat", ["../outside.txt"], false),
            ("cat", [@"\\server\share\file"], false),
            ("cat", ["D:file.txt"], false),
            ("cat", ["--file=../x"], false),
            ("dotnet", ["run"], false),
            ("dotnet", ["exec", "a.dll"], false),
            ("dotnet", ["--version", "extra"], false),
            ("find", [".", "-exec", "rm", "{}", ";"], false),
            ("where", ["cmd"], false),
            ("pwsh", ["-Command", "Get-ChildItem"], false),
            ("", [], false),
        ];
        int passed = 0;
        foreach ((string exe, string[] a, bool allowed) in cases)
        {
            string? refusal = Check(exe, a, scratch);
            bool ok = (refusal is null) == allowed;
            passed += ok ? 1 : 0;
            string call = $"{exe} {string.Join(' ', a)}".Trim();
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {call,-34} -> {(refusal is null ? "allowed" : "refused: " + refusal)}");
        }
        Console.WriteLine($"RESULT {passed}/{cases.Length}");
        return passed == cases.Length ? 0 : 1;
    }
}
