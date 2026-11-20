using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Ch15.RestartTests;

/// <summary>Where the built assemblies under test are: the same configuration as this harness.</summary>
internal static class Built
{
    // bin/<Configuration>/net10.0 of this project.
    private static readonly DirectoryInfo Output = new(AppContext.BaseDirectory);

    public static string ExternalEffects => Sibling("Ch15.ExternalEffects");
    public static string DurableReview => Sibling("Ch15.DurableReview");

    public static string ContosoV5
    {
        get
        {
            DirectoryInfo tfm = Output, configuration = tfm.Parent!;
            DirectoryInfo repo = configuration.Parent!.Parent!.Parent!.Parent!.Parent!;   // bin, project, chapter, src, root
            return Path.Combine(repo.FullName, "contoso", "v5-durable-resume", "bin", configuration.Name, tfm.Name,
                "Contoso.DurableResume.dll");
        }
    }

    private static string Sibling(string project) =>
        Path.Combine(Output.FullName.Replace("Ch15.RestartTests", project), project + ".dll");
}

internal sealed record Exited(int ExitCode, string Output, int Pid);

/// <summary>
/// A worker process: dotnet exec &lt;dll&gt; with stdin, stdout and stderr
/// redirected. The harness can wait for a line, kill the whole process tree
/// (no finally block, no flush, no lease release in the child), or let it go on.
/// </summary>
internal sealed class Worker : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly SemaphoreSlim _lineArrived = new(0);

    private Worker(Process process)
    {
        _process = process;
        _process.OutputDataReceived += (_, e) => Add(e.Data);
        _process.ErrorDataReceived += (_, e) => Add(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int Pid => _process.Id;
    public string Output => string.Join('\n', _lines);

    public static Worker Start(string dll, params string[] args)
    {
        ProcessStartInfo psi = new(DotnetHost())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        return new Worker(Process.Start(psi)!);
    }

    public static Exited Run(string dll, params string[] args)
    {
        using Worker w = Start(dll, args);
        return w.WaitForExit();
    }

    /// <summary>True when a line containing <paramref name="fragment"/> arrived before the timeout.</summary>
    public async Task<bool> WaitForLineAsync(string fragment, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            while (!_lines.Any(l => l.Contains(fragment, StringComparison.Ordinal)))
                await _lineArrived.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Kills the process where it stands and waits until it is gone.</summary>
    public Exited Kill()
    {
        _process.Kill(entireProcessTree: true);
        return WaitForExit();
    }

    /// <summary>Sends a line on stdin: a worker paused at a point goes on.</summary>
    public void Continue() => _process.StandardInput.WriteLine();

    public Exited WaitForExit()
    {
        if (!_process.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            return new Exited(-1, Output + "\n(timed out)", Pid);
        }
        _process.WaitForExit();      // drain the redirected streams
        return new Exited(_process.ExitCode, Output, Pid);
    }

    public void Dispose() => _process.Dispose();

    private void Add(string? line)
    {
        if (line is null) return;
        _lines.Enqueue(line);
        _lineArrived.Release();
    }

    private static string DotnetHost()
    {
        string? current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return current;
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(host) && File.Exists(host) ? host : "dotnet";
    }
}

/// <summary>Values read from a child's output: "label: value" lines and "operation &lt;guid&gt;".</summary>
internal static class Read
{
    public static string? Value(string output, string label) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(label, StringComparison.Ordinal))?[label.Length..].Trim();

    public static int Effects(string statusOutput) => int.TryParse(Value(statusOutput, "effects:"), out int n) ? n : -1;

    public static string[] References(string statusOutput) =>
        (Value(statusOutput, "references:") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string? Operation(string output)
    {
        string? line = output.Split('\n').FirstOrDefault(l => l.Contains("operation ", StringComparison.Ordinal));
        if (line is null) return null;
        string tail = line[(line.IndexOf("operation ", StringComparison.Ordinal) + "operation ".Length)..];
        return Guid.TryParse(tail.Split(' ', ')')[0], out Guid id) ? id.ToString("D") : null;
    }
}

/// <summary>
/// The "no shared in-memory state" check, done on the built assembly: every
/// static field that is neither const nor readonly, compiler-generated ones
/// (names with '&lt;') excepted. Read with System.Reflection.Metadata, so
/// nothing from the assembly is loaded or run.
/// </summary>
internal static class StaticFields
{
    public static IReadOnlyList<string> Mutable(string dll)
    {
        using FileStream stream = File.OpenRead(dll);
        using PEReader pe = new(stream);
        MetadataReader md = pe.GetMetadataReader();
        List<string> found = [];
        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition type = md.GetTypeDefinition(th);
            string typeName = md.GetString(type.Name);
            foreach (FieldDefinitionHandle fh in type.GetFields())
            {
                FieldDefinition field = md.GetFieldDefinition(fh);
                FieldAttributes a = field.Attributes;
                string name = md.GetString(field.Name);
                if ((a & FieldAttributes.Static) != 0 && (a & (FieldAttributes.Literal | FieldAttributes.InitOnly)) == 0 &&
                    !name.Contains('<') && !typeName.Contains('<'))
                    found.Add($"{typeName}.{name}");
            }
        }
        return found;
    }
}
