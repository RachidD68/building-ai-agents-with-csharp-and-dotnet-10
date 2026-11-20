// ============================================================================
// Kill switch: withdraw one capability, keep the service and its conversations
// ----------------------------------------------------------------------------
// During an incident the reimbursement tool is withdrawn while the read-only
// FAQ stays up. The switch is a registry read on every call, in code:
//
//   model asks for a tool ──> function-invocation middleware ──┐
//   HTTP request ──────────> endpoint filter (503) ────────────┼─> registry
//   any other path ────────> ReimbursementService (tool_revoked) <─ propagated
//
// The prompt is not a switch, and neither is hiding the tool from the model:
// both are reproduced below as failures before the fix.
//
// No model (a scripted client asks for the tools), no network beyond
// 127.0.0.1, no API key:
//   dotnet run -- --self-test     prints RESULT n/n, exit code 1 on any failure
// ============================================================================

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgenticAI.Testing;
using Ch08.ActionPolicy;
using Ch20.PolicyEnforcement;
using Ch24.KillSwitch;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

bool selfTest = args.Contains("--self-test");
Console.WriteLine(selfTest
    ? "=== Kill switch: self-test ==="
    : "=== Kill switch (no model; the same suite runs with --self-test) ===");
return await KillSwitchSuite.RunAsync();

internal static class KillSwitchSuite
{
    static int s_passed, s_total;
    static readonly Operator OnCallAna = new("ana (on call)", OnCall: true);
    static readonly Operator OnCallBen = new("ben (on call)", OnCall: true);
    static readonly Operator DevSam = new("sam (developer)", OnCall: false);
    const string Incident = "INC-0412";

    public static async Task<int> RunAsync()
    {
        DrillClock clock = new(World.Now);
        World world = new();
        CapabilityRegistry registry = NewRegistry(clock, world);
        ScriptedToolChatClient model = ScriptedUser();
        AIAgent agent = ContosoAssistant.Create(model, world, World.Alice).WithCapabilityGuard(registry);
        AgentSession session = await agent.CreateSessionAsync();
        await using ContosoApi api = new(world, registry);
        await api.StartAsync(new Dictionary<string, Caller> { ["tok-alice"] = World.Alice });

        Console.WriteLine("\n-- Before the incident --");
        await Ask(agent, session, "What do I need for a receipt?");
        Expect($"FAQ: {Last(model)}", Last(model).Contains("expense-policy#4"));
        Expect($"tool: {await Ask(agent, session, Claim("E-1001", 30m))}", Last(model) == "Allow low_value_policy");
        Expect($"HTTP POST /reimbursements: {await Post(api, "E-1001", 20m)}", api.LastStatus == HttpStatusCode.OK);
        int recordsBefore = world.Audit.Entries.Count;

        Console.WriteLine("\n-- Preserve the facts, then withdraw --");
        CapabilitySnapshot facts = registry.Snapshot();
        Expect($"a developer cannot pull the switch: {registry.Withdraw(Capabilities.Reimburse, DevSam, Incident, "bad answers")}",
            !registry.IsWithdrawn(Capabilities.Reimburse, out _));
        using (Barrier both = new(2))
        {
            Task<ChangeResult>[] pulls = [.. new[] { OnCallAna, OnCallBen }.Select(op => Task.Run(() =>
            {
                both.SignalAndWait();
                return registry.Withdraw(Capabilities.Reimburse, op, Incident, "answers degraded after re-index");
            }))];
            ChangeResult[] r = await Task.WhenAll(pulls);
            Expect($"two on-call operators withdraw at once (Task.Run + Barrier): {string.Join(", ", r.Order())}; one 'withdraw' audited",
                r.Count(x => x == ChangeResult.Changed) == 1 && r.Count(x => x == ChangeResult.AlreadyInState) == 1 &&
                registry.Audit.Count(a => a.Action == "withdraw") == 1);
        }
        Expect($"the snapshot taken first still reads reimburse={facts.States[Capabilities.Reimburse]}, faq={facts.States[Capabilities.Faq]}",
            facts.States[Capabilities.Reimburse] == "enabled");

        Console.WriteLine("\n-- The same conversation while reimburse is withdrawn --");
        decimal balance = world.Accounts.Get("E-1001").ReimbursableBalance;
        Expect($"tool: {await Ask(agent, session, Claim("E-1001", 10m))}",
            Last(model).StartsWith("capability_withdrawn", StringComparison.Ordinal) &&
            world.Accounts.Get("E-1001").ReimbursableBalance == balance && world.Audit.Entries.Count == recordsBefore);
        await Ask(agent, session, "And for a receipt again?");
        Expect($"FAQ in the same session: {Last(model)} (the model still sees the first question)",
            Last(model).Contains("expense-policy#4") &&
            model.Turns[^1].Messages.Any(m => m.Text == "What do I need for a receipt?"));
        Expect($"HTTP POST /reimbursements: {(int)(await PostRaw(api, "E-1001", 10m))} capability_withdrawn; GET /faq: {(int)await Faq(api)}",
            api.LastStatus == HttpStatusCode.ServiceUnavailable && api.LastBody.Contains("capability_withdrawn") &&
            !api.LastBody.Contains("re-index", StringComparison.Ordinal) &&
            await Faq(api) == HttpStatusCode.OK);
        Outcome direct = world.Service.Reimburse("mcp", World.Alice, new(Guid.NewGuid(), "E-1001", 10m, "EUR"));
        Expect($"a path that skips both guards meets the decision point: {direct}", direct.Reason == "tool_revoked");

        {
            World w = new();
            CapabilityRegistry reg = NewRegistry(clock, w);
            AIAgent[] agents = [.. Enumerable.Range(0, 20).Select(_ =>
                ContosoAssistant.Create(ScriptedUser(), w, World.Alice).WithCapabilityGuard(reg))];
            reg.Withdraw(Capabilities.Reimburse, OnCallAna, Incident, "drill");
            string[] replies = await Task.WhenAll(agents.Select(a => Task.Run(async () => (await a.RunAsync(Claim("E-1001", 5m))).Text)));
            Expect($"20 concurrent runs (Task.Run) after the withdrawal: {replies.Count(r => r.Contains("capability_withdrawn"))} refused, " +
                $"balance {w.Accounts.Get("E-1001").ReimbursableBalance}",
                replies.All(r => r.Contains("capability_withdrawn")) && w.Accounts.Get("E-1001").ReimbursableBalance == 500m);
        }
        {
            World w = new();
            CapabilityRegistry reg = NewRegistry(clock, w);
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            AIAgent held = ContosoAssistant.Create(ScriptedUser(), w, World.Alice,
                beforeEffect: async () => { entered.SetResult(); await release.Task; }).WithCapabilityGuard(reg);
            Task<AgentResponse> inFlight = Task.Run(async () => await held.RunAsync(Claim("E-1001", 10m)));
            await entered.Task;                                   // past the tool-side check, not yet at the effect
            reg.Withdraw(Capabilities.Reimburse, OnCallAna, Incident, "drill");
            release.SetResult();
            string reply = (await inFlight).Text;
            Expect($"a call already past the tool check when the switch is pulled: {reply}",
                reply.Contains("Deny tool_revoked") && w.Accounts.Get("E-1001").ReimbursableBalance == 500m);
        }
        {
            bool ran = false;
            [System.ComponentModel.Description("Export the whole expense history.")]
            string ExportHistory() { ran = true; return "exported"; }
            ScriptedToolChatClient m = null!;
            m = new ScriptedToolChatClient(t => t.Results.Count == 0 ? m.Call("export_history") : ScriptedToolChatClient.Say("done"));
            AIAgent extra = new ChatClientAgent(m, tools: [AIFunctionFactory.Create(ExportHistory, name: "export_history")])
                .WithCapabilityGuard(registry);
            await extra.RunAsync("export everything");
            Expect($"a tool nobody registered is refused (fail closed): {Last(m)}",
                !ran && Last(m).StartsWith("capability_withdrawn", StringComparison.Ordinal) &&
                registry.IsWithdrawn("export_history", out string why) && why == "not registered");
        }
        {
            World w = new();
            CapabilityRegistry reg = NewRegistry(clock, w);
            const int Races = 5000;
            int disagree = 0;
            for (int i = 0; i < Races; i++)
            {
                if (i % 2 == 0) reg.Withdraw(Capabilities.Reimburse, OnCallAna, Incident, "race");
                else reg.Enable(Capabilities.Reimburse, OnCallAna, Incident, "eval-race");
                using Barrier both = new(2);
                await Task.WhenAll(
                    Task.Run(() => { both.SignalAndWait(); reg.Enable(Capabilities.Reimburse, OnCallBen, Incident, "eval-race"); }),
                    Task.Run(() => { both.SignalAndWait(); reg.Withdraw(Capabilities.Reimburse, OnCallAna, Incident, "race"); }));
                if (reg.IsWithdrawn(Capabilities.Reimburse, out _) != w.Revocations.IsToolRevoked(Capabilities.Reimburse)) disagree++;
            }
            Expect($"{Races} withdraw-vs-enable races (Task.Run + Barrier): registry and effect disagree in {disagree}", disagree == 0);
        }
        {
            World w = new();
            CapabilityRegistry reg = NewRegistry(clock, w);
            w.Revocations.RevokeTool(Capabilities.Reimburse, "SEC-77: tool token leaked");   // the security chapter's own revocation
            reg.Withdraw(Capabilities.Reimburse, OnCallAna, Incident, "drill");
            reg.Enable(Capabilities.Reimburse, OnCallAna, Incident, "eval-2026-11-20-r2");
            Outcome o = w.Service.Reimburse("mcp", World.Alice, new(Guid.NewGuid(), "E-1001", 10m, "EUR"));
            Expect($"a security revocation outlives the switch's withdraw and enable: switch enabled, direct path {o}",
                !reg.IsWithdrawn(Capabilities.Reimburse, out _) && o.Reason == "tool_revoked");
        }

        Console.WriteLine("\n-- Where it fails --");
        {
            World w = new();
            AIAgent careful = ContosoAssistant.Create(ScriptedUser(), w, World.Alice,
                instructions: "Reimbursements are paused during an incident. Be careful: do NOT call the reimburse tool.");
            string reply = (await careful.RunAsync(Claim("E-1001", 30m))).Text;
            Expect($"DEFECT REPRODUCED: a 'be careful' instruction as the switch; the model calls the tool anyway: {reply}, " +
                $"balance {w.Accounts.Get("E-1001").ReimbursableBalance}",
                reply.Contains("Allow") && w.Accounts.Get("E-1001").ReimbursableBalance == 470m);
        }
        {
            World w = new();
            CapabilityRegistry reg = new(clock);
            reg.Register(Capabilities.Faq, Capabilities.Reimburse);
            reg.Withdraw(Capabilities.Reimburse, OnCallAna, Incident, "drill");
            ScriptedToolChatClient m = ScriptedUser();
            AIAgent hidden = ContosoAssistant.Create(new HideWithdrawnToolsChatClient(m, reg), w, World.Alice);
            string reply = (await hidden.RunAsync(Claim("E-1001", 30m))).Text;
            Expect($"DEFECT REPRODUCED: tool hidden from the model (shown: {string.Join(", ", m.Turns[0].ToolNames)}); " +
                $"it asks by name and the function runs: {reply}",
                !m.Turns[0].ToolNames.Contains(Capabilities.Reimburse) && reply.Contains("Allow") &&
                w.Accounts.Get("E-1001").ReimbursableBalance == 470m);

            ScriptedToolChatClient m2 = ScriptedUser();
            AIAgent both = ContosoAssistant.Create(new HideWithdrawnToolsChatClient(m2, reg), w, World.Alice).WithCapabilityGuard(reg);
            reply = (await both.RunAsync(Claim("E-1001", 30m))).Text;
            Expect($"fix: hidden AND guarded, the same request is refused: {Last(m2)}",
                Last(m2).StartsWith("capability_withdrawn", StringComparison.Ordinal) && w.Accounts.Get("E-1001").ReimbursableBalance == 470m);
        }

        Console.WriteLine("\n-- Re-enable after re-evaluation --");
        clock.Advance(TimeSpan.FromMinutes(40));
        Expect($"enable with no evaluation run: {registry.Enable(Capabilities.Reimburse, OnCallAna, Incident, "")}",
            registry.IsWithdrawn(Capabilities.Reimburse, out _));
        Expect($"enable with eval-2026-11-20-r2: {registry.Enable(Capabilities.Reimburse, OnCallAna, Incident, "eval-2026-11-20-r2")}",
            !registry.IsWithdrawn(Capabilities.Reimburse, out _));
        Expect($"tool, same session as before: {await Ask(agent, session, Claim("E-1001", 10m))}", Last(model) == "Allow low_value_policy");
        Expect($"HTTP POST /reimbursements: {await Post(api, "E-1001", 15m)}", api.LastStatus == HttpStatusCode.OK);

        Console.WriteLine("\n-- The record --");
        foreach (CapabilityChange c in registry.Audit)
            Console.WriteLine($"  #{c.Seq} {c.At:HH:mm} {c.Capability} {c.Action} by {c.Actor} [{c.Incident}] {c.Detail}");
        Expect($"{registry.Audit.Count} audited changes, refused attempts included; denials: " +
            string.Join(", ", registry.Denials.GroupBy(d => d.Surface).Select(g => $"{g.Key} {g.Count()}")),
            registry.Audit.Select(a => a.Action).SequenceEqual(["withdraw refused", "withdraw", "enable refused", "enable"]));
        AuditEntry[] paid = [.. world.Audit.Entries.Where(e => e.EmployeeId == "E-1001" && e.Decision == Decision.Allow)];
        Expect($"operation records intact: {paid.Length} payments ({string.Join(", ", paid.Select(p => $"{p.Transport} {p.Amount.ToString(CultureInfo.InvariantCulture)}"))}), " +
            $"balance {world.Accounts.Get("E-1001").ReimbursableBalance}",
            paid.Select(p => p.Amount).SequenceEqual([30m, 20m, 10m, 15m]) && world.Accounts.Get("E-1001").ReimbursableBalance == 425m);

        Console.WriteLine($"\nRESULT {s_passed}/{s_total}");
        return s_passed == s_total ? 0 : 1;
    }

    static CapabilityRegistry NewRegistry(TimeProvider clock, World world)
    {
        CapabilityRegistry registry = new(clock);
        registry.Register(Capabilities.Faq, Capabilities.Reimburse);
        registry.Propagate((capability, withdrawn, reason) =>
        {
            if (capability != Capabilities.Reimburse) return;
            // Its own revocation source: an enable lifts this switch's revocation, never security's.
            if (withdrawn) world.Revocations.RevokeTool(capability, "ops-switch", reason);
            else world.Revocations.RestoreTool(capability, "ops-switch");
        });
        return registry;
    }

    /// <summary>
    /// A scripted model standing in for one that does whatever the user asks:
    /// "reimburse E-1001 30 EUR {id}" calls reimburse, anything else calls
    /// search_faq, and a tool result is read back as "Tool said: ...".
    /// It ignores its instructions on purpose; that is the failure to reproduce.
    /// </summary>
    static ScriptedToolChatClient ScriptedUser()
    {
        ScriptedToolChatClient model = null!;
        model = new ScriptedToolChatClient(turn =>
        {
            int user = turn.Messages.ToList().FindLastIndex(m => m.Role == ChatRole.User);
            FunctionResultContent? result = turn.Messages.Skip(user + 1).SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>().LastOrDefault();
            if (result is not null) return ScriptedToolChatClient.Say($"Tool said: {result.Result?.ToString()?.Trim('"')}");
            string[] w = turn.Messages[user].Text.Split(' ');
            return w[0] == "reimburse"
                ? model.Call(Capabilities.Reimburse, new Dictionary<string, object?>
                {
                    ["employeeId"] = w[1], ["amount"] = decimal.Parse(w[2], CultureInfo.InvariantCulture),
                    ["currency"] = w[3], ["operationId"] = w[4],
                })
                : model.Call(Capabilities.Faq, new Dictionary<string, object?> { ["question"] = turn.Messages[user].Text });
        });
        return model;
    }

    static string Claim(string employee, decimal amount) =>
        $"reimburse {employee} {amount.ToString(CultureInfo.InvariantCulture)} EUR {Guid.NewGuid()}";

    static async Task<string> Ask(AIAgent agent, AgentSession session, string text) => (await agent.RunAsync(text, session)).Text;

    /// <summary>The last tool result the model saw.</summary>
    static string Last(ScriptedToolChatClient model) =>
        model.Turns[^1].Results.LastOrDefault()?.Result?.ToString()?.Trim('"') ?? "(no tool result)";

    static async Task<string> Post(ContosoApi api, string employee, decimal amount)
    {
        HttpStatusCode status = await PostRaw(api, employee, amount);
        return $"{(int)status} {api.LastBody}";
    }

    static async Task<HttpStatusCode> PostRaw(ContosoApi api, string employee, decimal amount)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/reimbursements")
        {
            Content = JsonContent.Create(new ReimbursementCommand(Guid.NewGuid(), employee, amount, "EUR")),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok-alice");
        using HttpResponseMessage response = await api.Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        api.LastStatus = response.StatusCode;
        api.LastBody = response.StatusCode == HttpStatusCode.OK
            ? (System.Text.Json.JsonSerializer.Deserialize<Outcome>(body, System.Text.Json.JsonSerializerOptions.Web)?.ToString() ?? body)
            : body;
        return response.StatusCode;
    }

    static async Task<HttpStatusCode> Faq(ContosoApi api)
    {
        using HttpResponseMessage response = await api.Http.GetAsync("/faq?q=receipt");
        return response.StatusCode;
    }

    static void Expect(string name, bool ok)
    {
        s_total++;
        if (ok) s_passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }
}

internal sealed class DrillClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
