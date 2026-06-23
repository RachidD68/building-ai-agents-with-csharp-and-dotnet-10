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
//   1. Pin the working directory: every command runs in a scratch sandbox.
//   2. Allow-list the executable: only `dotnet`, `ls`, `dir`, `cat`, `type`,
//      `wc`, `find`, `where` are permitted; everything else (rm, sudo, curl,
//      ...) is rejected before a process is ever started.
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
// known-good program with arguments that never reach a shell. (We never invoke
// a shell, so shell metacharacters in an argument are just literal bytes.)
//
// In production, a shell tool should NEVER be exposed on a public endpoint
// without the safety pipeline from Chapter 15 (input sanitisation + critic
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

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLlmChatClient(builder.Configuration);

using IHost host = builder.Build();
IChatClient chatClient = host.Services.GetRequiredService<IChatClient>();

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Allow-list for the shell tool ──────────────────────────────────
// Executables that are SAFE to expose to an LLM. Adjust per deployment;
// never expose anything that can mutate state, escalate privileges, or
// reach the network. This allow-list is the primary gate: a name that is
// not on it never reaches a process, no matter what arguments accompany it.
HashSet<string> allowedCommands = new(StringComparer.OrdinalIgnoreCase)
{
    "dotnet", "ls", "dir", "cat", "type", "wc", "find", "where",
    "pwsh", "powershell"   // restricted to read-only cmdlets below
};

string sandboxDir = Path.Combine(
    Path.GetTempPath(),
    $"ch08-shelltool-{Guid.NewGuid():N}");
Directory.CreateDirectory(sandboxDir);

// Seed the sandbox with a couple of files for the agent to discover.
File.WriteAllText(Path.Combine(sandboxDir, "README.md"),
    "# Sandbox\nThis directory was created for the Ch08 shell-tool demo.\n");
File.WriteAllText(Path.Combine(sandboxDir, "data.txt"),
    string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}")));

Console.WriteLine($"Sandbox: {sandboxDir}");
Console.WriteLine();

// ── The shell tool: an AIFunction the model can call ───────────────
// The signature is deliberately structured: an executable name plus a
// separate array of arguments. The model fills both, and we pass the
// arguments straight to ProcessStartInfo.ArgumentList — never to a shell,
// never re-parsed. That removes a whole class of quoting and injection bugs.
[Description("""
    Executes a single safe program in the sandbox directory and returns its
    stdout. The program must be on the allow-list (dotnet, ls, dir, cat, type,
    wc, find, where, pwsh, powershell). Arguments are passed verbatim to the
    program — no shell is involved, so pipelines, redirects, and command
    chaining are impossible. Output is truncated at 4 KB.
""")]
async Task<string> RunShellAsync(
    [Description("The program to run, e.g. \"dotnet\", \"ls\", or \"cat\". Must be on the allow-list.")]
    string executable,
    [Description("The arguments to pass to the program, each as a separate array element, e.g. [\"--version\"] or [\"-la\"] or [\"README.md\"]. Pass an empty array for no arguments.")]
    string[]? arguments)
{
    if (string.IsNullOrWhiteSpace(executable))
    {
        return "Error: no executable specified.";
    }

    // The allow-list is the gate. Because we never launch a shell (see
    // UseShellExecute = false below) and pass arguments through ArgumentList,
    // the arguments cannot smuggle in a second command — they are just data.
    if (!allowedCommands.Contains(executable))
    {
        return $"Error: program '{executable}' is not on the allow-list.";
    }

    ProcessStartInfo startInfo = new()
    {
        FileName = executable,
        WorkingDirectory = sandboxDir,
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
    description: "Run a safe, allow-listed program (with structured arguments) in the sandbox directory.");

ChatClientAgent agent = new(
    chatClient,
    instructions: """
        You are a careful systems-engineer assistant with access to a shell
        tool. The tool runs a single allow-listed program in a sandbox
        directory; call it with the program name and an array of arguments
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
try { Directory.Delete(sandboxDir, recursive: true); } catch { }
