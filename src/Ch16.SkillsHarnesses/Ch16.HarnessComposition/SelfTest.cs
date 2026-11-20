using AgenticAI.Testing;
using Ch16.Harness;
using Ch16.HarnessComposition;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>Offline checks: dotnet run -- --self-test. A scripted client stands in for the model.</summary>
static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("=== Harness composition self-test (offline) ===\n");
        int passed = 0, total = 0;
        void Check(string name, bool ok)
        {
            total++; passed += ok ? 1 : 0;
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
        }

        string original = Directory.GetCurrentDirectory();
        string work = Path.Combine(Path.GetTempPath(), "ch16-compose-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        Directory.SetCurrentDirectory(work);   // no skills folder, nothing written beside the project
        try
        {
            ScriptedToolChatClient full = await AskAsync(Composition.Full());
            ScriptedToolChatClient minimal = await AskAsync(Composition.Minimal());
            var fullSize = CapabilityReport.Size(full.Turns[0].Options);
            var minimalSize = CapabilityReport.Size(minimal.Turns[0].Options);
            Console.WriteLine($"  full    : {fullSize.Instructions} instruction chars, {fullSize.Tools} tools, {fullSize.SchemaChars} schema chars");
            Console.WriteLine($"  minimal : {minimalSize.Instructions} instruction chars, {minimalSize.Tools} tools, {minimalSize.SchemaChars} schema chars");

            Check("minimal harness sends exactly one tool: CheckShipping",
                minimal.Turns[0].ToolNames.SequenceEqual(["CheckShipping"]));
            Check("full harness sends CheckShipping beside the harness's own tools (to-do, modes)",
                full.Turns[0].ToolNames.Contains("CheckShipping") && full.Turns[0].ToolNames.Contains("todos_add") &&
                full.Turns[0].ToolNames.Contains("mode_set"));
            Check("HarnessInstructions = \"\" sends only ChatOptions.Instructions (\"Be brief.\")",
                minimal.Turns[0].Options?.Instructions == "Be brief.");
            Check("minimal request is smaller in both instruction and schema characters",
                minimalSize.Instructions < fullSize.Instructions && minimalSize.SchemaChars < fullSize.SchemaChars);
            Check("both agents called CheckShipping and got the same tool result",
                new[] { full, minimal }.All(c => c.Turns[^1].Results.Any(r => r.Result?.ToString()?.Contains("in transit") == true)));

            // A model that never stops calling the tool.
            ScriptedToolChatClient loop = null!;
            loop = new(_ => loop.Call("CheckShipping", new Dictionary<string, object?> { ["orderId"] = "ORD-3003" }));
            HarnessAgent bounded = loop.AsHarnessAgent(Composition.Minimal());
            await bounded.RunAsync("Check ORD-3003.", await bounded.CreateSessionAsync());
            Console.WriteLine($"  a model that always calls the tool: {loop.Turns.Count} model calls with MaximumIterationsPerRequest = 5");
            Check("MaximumIterationsPerRequest = 5 bounds a model that never stops calling tools",
                loop.Turns.Count <= 6);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine($"\nRESULT {passed}/{total}");
        return passed == total ? 0 : 1;
    }

    private static async Task<ScriptedToolChatClient> AskAsync(HarnessAgentOptions options)
    {
        ScriptedToolChatClient client = null!;
        client = new(turn => turn.Results.Count == 0
            ? client.Call("CheckShipping", new Dictionary<string, object?> { ["orderId"] = "ORD-3003" })
            : ScriptedToolChatClient.Say("It is in transit, due Thursday."));
        HarnessAgent agent = client.AsHarnessAgent(options);
        await agent.RunAsync("Is order ORD-3003 going to arrive this week?", await agent.CreateSessionAsync());
        return client;
    }
}
