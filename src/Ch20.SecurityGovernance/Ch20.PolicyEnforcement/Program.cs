// ============================================================================
// Policy enforcement at the effect, on every transport
// ----------------------------------------------------------------------------
// Detection layers (filters, detectors, a critic) are signals. The decision is
// made here, by ReimbursementService, at the point where money would move:
//
//   HTTP API ─┐
//   MCP tool ─┤   Caller from that transport's auth context
//   A2A run  ─┼──> ReimbursementService.Reimburse ──> decision + debit
//   AG-UI run─┘   (revocation, Ch08's policy, added rules, idempotency)
//
// Added rules start in Observe (record "would deny", let it through) and are
// switched to Enforce once measured. Revocation is read at every decision.
//
// No model, no network beyond 127.0.0.1, no API key:
//   dotnet run -- --self-test     prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using Ch08.ActionPolicy;
using Ch20.PolicyEnforcement;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Policy enforcement: self-test ==="
    : "=== Policy enforcement (no model; the same suite runs with --self-test) ===");
return await PolicySuite.RunAsync();

internal static class PolicySuite
{
    static int s_passed, s_total;

    public static async Task<int> RunAsync()
    {
        World world = new();
        await using FourTransports transports = new(world);
        await transports.StartAsync();

        async Task Parity(string name, Caller caller, string employee, decimal amount, string expected)
        {
            List<string> results = [];
            foreach (string t in FourTransports.Names)
                results.Add($"{t}={await transports.CallAsync(t, caller, new(Guid.NewGuid(), employee, amount, "EUR"))}");
            Expect($"{name}: {expected} on {string.Join(", ", FourTransports.Names)}",
                results.All(r => r.EndsWith("=" + expected, StringComparison.Ordinal)), results);
        }

        Console.WriteLine("\n-- One decision, four transports --");
        await Parity("alice, own tenant, 30 EUR", World.Alice, "E-1001", 30m, "Allow low_value_policy");
        await Parity("HARMLESS WORDING, FORBIDDEN ACTION: alice, E-2002 (fabrikam), 45 EUR", World.Alice, "E-2002", 45m, "Deny resource_scope");
        Expect($"E-2002's balance is untouched: {world.Accounts.Get("E-2002").ReimbursableBalance}",
            world.Accounts.Get("E-2002").ReimbursableBalance == 500m);
        await Parity("negative amount", World.Alice, "E-1001", -10m, "Deny invalid_amount");
        await Parity("unknown employee answers like a foreign one", World.Alice, "E-9999", 10m, "Deny resource_scope");
        await Parity("80 EUR needs an approval", World.Alice, "E-1001", 80m, "RequireApproval approval_missing");
        await Parity("bob has no submit permission", World.Bob, "E-1001", 20m, "Deny missing_permission");
        Expect("the tenantId sent in every request body and tool call was never read (E-2002 stayed denied)",
            world.Audit.Entries.Where(e => e.EmployeeId == "E-2002").All(e => e.TenantId == "contoso" && e.Decision == Decision.Deny));

        Console.WriteLine("\n-- Observe, then enforce --");
        AuditEntry[] paid = [.. world.Audit.Entries.Where(e => e.EmployeeId == "E-1001" && e.Decision == Decision.Allow)];
        Expect($"observe: 4 paid, the 3rd and 4th recorded as would-deny daily_limit; balance {world.Accounts.Get("E-1001").ReimbursableBalance}",
            paid.Length == 4 && paid.Count(e => e.WouldDeny.Contains(RuleModes.DailyLimit)) == 2 &&
            world.Accounts.Get("E-1001").ReimbursableBalance == 380m);
        world.Modes.Set(RuleModes.DailyLimit, RuleMode.Enforce);
        await Parity("enforce: the same rule now denies", World.Alice, "E-1001", 10m, "Deny daily_limit");
        Expect("a baseline rule cannot be put in observe mode", Throws<ArgumentException>(() => world.Modes.Set("resource_scope", RuleMode.Observe)));

        Console.WriteLine("\n-- Revocation reaches open sessions --");
        world.Revocations.RevokeTool(ReimbursementService.Tool, "incident drill");
        await Parity("tool revoked after the MCP sessions opened", World.Alice, "E-1002", 10m, "Deny tool_revoked");
        world.Revocations.RestoreTool(ReimbursementService.Tool);
        world.Revocations.RevokeSubject("contoso", "alice", "offboarded");
        (System.Net.HttpStatusCode status, string outcome) = await transports.HttpRawAsync(World.Alice, new(Guid.NewGuid(), "E-1002", 10m, "EUR"));
        Expect($"subject revoked, token still valid: HTTP {(int)status} {outcome} (authenticated, then denied)",
            status == System.Net.HttpStatusCode.Forbidden && outcome == "Deny subject_revoked");
        await Parity("subject revoked", World.Alice, "E-1002", 10m, "Deny subject_revoked");
        world.Revocations.RestoreSubject("contoso", "alice");

        Console.WriteLine("\n-- Idempotency across transports --");
        ReimbursementCommand once = new(Guid.NewGuid(), "E-1002", 10m, "EUR");
        string first = await transports.CallAsync("http", World.Alice, once);
        string again = await transports.CallAsync("mcp", World.Alice, once);
        Expect($"same operation id over HTTP then MCP: {first}; {again}",
            first == "Allow low_value_policy" && again == "Deny duplicate_operation" &&
            world.Accounts.Get("E-1002").ReimbursableBalance == 90m);

        Console.WriteLine("\n-- Concurrent claims on one balance --");
        {
            World fresh = new();   // E-1002: 100 EUR
            Task<Outcome>[] claims = [.. Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
                fresh.Service.Reimburse("http", World.Alice, new(Guid.NewGuid(), "E-1002", 30m, "EUR"))))];
            Outcome[] outcomes = await Task.WhenAll(claims);
            Expect($"10 concurrent 30 EUR claims (Task.Run): {outcomes.Count(o => o.Decision == Decision.Allow)} allowed, " +
                $"balance {fresh.Accounts.Get("E-1002").ReimbursableBalance}",
                outcomes.Count(o => o.Decision == Decision.Allow) == 3 && fresh.Accounts.Get("E-1002").ReimbursableBalance == 10m);
        }
        {
            World fresh = new();
            TaskCompletionSource allChecked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int checkedCount = 0;
            Task Between() { if (Interlocked.Increment(ref checkedCount) == 10) allChecked.SetResult(); return allChecked.Task; }
            bool[] ok = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
                CheckThenAct.ReimburseAsync(fresh.Accounts, World.Alice, new(Guid.NewGuid(), "E-1002", 30m, "EUR"), Between, World.Now))));
            Expect($"DEFECT REPRODUCED: check, await, then debit: {ok.Count(x => x)} of 10 pass, balance {fresh.Accounts.Get("E-1002").ReimbursableBalance}",
                ok.All(x => x) && fresh.Accounts.Get("E-1002").ReimbursableBalance == -200m);
        }

        Console.WriteLine("\n-- A second transport that skips the check --");
        {
            World fresh = new();
            (IHost host, ModelContextProtocol.Client.McpClient client) = await FourTransports.StartUnguardedMcpAsync(fresh.Accounts);
            string result = await FourTransports.McpAsync(client, new(Guid.NewGuid(), "E-2002", 45m, "EUR"));
            Expect($"DEFECT REPRODUCED: an MCP tool that calls the ledger directly pays fabrikam's E-2002: {result}, " +
                $"balance {fresh.Accounts.Get("E-2002").ReimbursableBalance}",
                result == "Allow posted" && fresh.Accounts.Get("E-2002").ReimbursableBalance == 455m);
            await client.DisposeAsync();
            await host.StopAsync();
            host.Dispose();
        }

        Console.WriteLine("\n-- Audit --");
        IReadOnlyList<AuditEntry> audit = world.Audit.Entries;
        Expect($"{audit.Count} decisions audited, each with its transport ({string.Join(", ", audit.GroupBy(e => e.Transport).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))})",
            audit.Count == 4 * 9 + 1 + 2 && audit.All(e => FourTransports.Names.Contains(e.Transport)));

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    static void Expect(string name, bool ok, IEnumerable<string>? detail = null)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok && detail is not null) Console.WriteLine($"        {string.Join("; ", detail)}");
    }
}
