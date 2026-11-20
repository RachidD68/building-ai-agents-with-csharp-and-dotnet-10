using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ch12.ReceiptExtraction;
using Microsoft.Extensions.AI;

namespace Contoso.DurableResume;

/// <summary>
/// The receipt reading for prepare without --live: what an honest reader
/// reports for the clean taxi receipt of the corpus (the same values as v4's
/// scripted vision for taxi-clean.png). No network.
/// </summary>
internal sealed class ScriptedReceiptReading : IChatClient
{
    private static string F(string text) => JsonSerializer.Serialize(new ReadField(text, "present"), ReceiptReader.Json);

    private static readonly string Reply = $$"""
        {"vendor":{{F("NORTHWIND TAXI")}},"date":{{F("2026-11-12")}},"currency":{{F("EUR")}},
         "lines":[{"description":"Fare","amount":{{F("58.30")}}},{"description":"Airport fee","amount":{{F("6.50")}}}],
         "total":{{F("64.80")}},"amountCharged":{{JsonSerializer.Serialize(new ReadField("", "absent"), ReceiptReader.Json)}},
         "printedInstructions":[]}
        """;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, Reply);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>
/// --self-test: prepare, review and resume as separate child processes of
/// this assembly against fresh temp stores. The kill-at-each-point proof is
/// Ch15.RestartTests. Prints RESULT n/n.
/// </summary>
internal static class V5SelfTest
{
    public static int Run()
    {
        Console.WriteLine("=== Contoso v5: durable resume (self-test, offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }
        string root = Path.Combine(Path.GetTempPath(), $"contoso-v5-selftest-{Guid.NewGuid():N}");
        try
        {
            Console.WriteLine("-- 1. Alice's taxi claim waits on disk; rita decides; a third process pays");
            string store = Path.Combine(root, "happy");
            Child prepare = Child.Run(store, "prepare");
            Check("prepare: the receipt claim is AwaitingReview (approval_missing) and the process exits",
                prepare.ExitCode == 0 && prepare.Output.Contains("Intake: AwaitingReview (approval_missing)") &&
                prepare.Output.Contains("Claim:"));
            Child early = Child.Run(store, "resume", "latest");
            Check("resume before a decision waits (exit 5), no payment",
                early.ExitCode == 5 && early.Output.Contains("WAITING") && Effects(store) == 0);
            Child own = Child.Run(store, "review", "latest", "E-1001", "approve");
            Child bob = Child.Run(store, "review", "latest", "E-2002", "approve");
            Check("alice cannot approve her own claim; bob holds no reviewer role",
                own.Output.Contains("REFUSED separation_of_duties") && bob.Output.Contains("REFUSED reviewer_not_authorized"));
            Child review = Child.Run(store, "review", "latest", "rita", "approve");
            Check("rita's decision is recorded for 64.80 EUR at account version 7",
                review.ExitCode == 0 && review.Output.Contains("for 64.80 EUR at account version 7"));
            Child upgraded = Child.Run(store, "resume", "latest", "--graph", "next");
            Check("a build with another graph -> BLOCKED graph_version_mismatch, no payment",
                upgraded.ExitCode == 4 && upgraded.Output.Contains("BLOCKED graph_version_mismatch") && Effects(store) == 0);
            Child resume = Child.Run(store, "resume", "latest");
            Check("resume restores the paused review in a new process and the v2 desk pays: Delivered",
                resume.ExitCode == 0 && resume.Output.Contains("Restored checkpoint") && resume.Output.Contains("[DELIVERED]"));
            Check("prepare, review and resume were three different processes",
                new[] { prepare.Pid, review.Pid, resume.Pid, Environment.ProcessId }.Distinct().Count() == 4);
            Child again = Child.Run(store, "resume", "latest");
            Check("a second resume re-reads the claim: still Delivered, still one payment",
                again.ExitCode == 0 && again.Output.Contains("re-read the claim: already Delivered") && Effects(store) == 1);

            Console.WriteLine("\n-- 2. Where resume must stop");
            string revoked = Path.Combine(root, "revoked");
            Child.Run(revoked, "prepare");
            Child.Run(revoked, "review", "latest", "rita", "approve");
            Child.Run(revoked, "revoke", "rita");
            Child gone = Child.Run(revoked, "resume", "latest");
            Check("rita's role revoked after her decision -> BLOCKED reviewer_revoked, no payment",
                gone.ExitCode == 4 && gone.Output.Contains("BLOCKED reviewer_revoked") && Effects(revoked) == 0);

            string stale = Path.Combine(root, "stale");
            Child.Run(stale, "prepare");
            Child.Run(stale, "review", "latest", "rita", "approve");
            Child.Run(stale, "account-changed", "E-1001");
            Child moved = Child.Run(stale, "resume", "latest");
            Check("the account moved after the decision -> BLOCKED stale_approval, no payment",
                moved.ExitCode == 4 && moved.Output.Contains("BLOCKED stale_approval") && Effects(stale) == 0);

            string expired = Path.Combine(root, "expired");
            Child.Run(expired, "prepare");
            Child.Run(expired, "review", "latest", "rita", "approve", "--now", "2026-11-20T09:00:00Z");
            Child late = Child.Run(expired, "resume", "latest", "--now", "2026-11-21T10:00:00Z");
            Check("resumed 25 hours after the decision -> BLOCKED approval_expired, no payment",
                late.ExitCode == 4 && late.Output.Contains("BLOCKED approval_expired") && Effects(expired) == 0);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static int Effects(string store) => DurableClaims.JournalReferences(Path.Combine(store, "provider.db")).Count;
}

internal sealed record Child(int ExitCode, string Output, int Pid)
{
    public static Child Run(string store, params string[] args)
    {
        ProcessStartInfo psi = new(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(typeof(Child).Assembly.Location);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--store");
        psi.ArgumentList.Add(store);
        using Process p = Process.Start(psi)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromSeconds(120)))
        {
            p.Kill(entireProcessTree: true);
            return new Child(-1, "timed out", p.Id);
        }
        return new Child(p.ExitCode, stdout.Result + stderr.Result, p.Id);
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
