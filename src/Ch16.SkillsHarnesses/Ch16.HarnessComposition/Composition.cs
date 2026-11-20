using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch16.HarnessComposition;

/// <summary>The two harness shapes the chapter compares, built from the same custom tool.</summary>
public static class Composition
{
    [Description("Looks up the shipping status for a Contoso order id.")]
    public static string CheckShipping(
        [Description("The Contoso order id, for example ORD-3003.")] string orderId) =>
        orderId.EndsWith('3') ? $"{orderId}: in transit, due Thursday." : $"{orderId}: delivered.";

    public static AIFunction ShippingTool { get; } = AIFunctionFactory.Create(CheckShipping);

    /// <summary>Everything on except what a local model cannot serve or what writes to disk.</summary>
    public static HarnessAgentOptions Full() => new()
    {
        Name = "FullHarness",
        HarnessInstructions = "You work Contoso's support desk. Use CheckShipping for delivery questions.",
        ChatOptions = new ChatOptions { Instructions = "Be brief.", Tools = [ShippingTool] },
        DisableWebSearch = true,      // a local provider cannot serve hosted search
        DisableFileMemory = true,     // leave nothing on disk
    };

    /// <summary>The smallest useful harness: one custom tool, approvals kept, the loop bounded.</summary>
    public static HarnessAgentOptions Minimal() => new()
    {
        Name = "MinimalHarness",
        HarnessInstructions = "",     // "" sends no harness text; null would send DefaultInstructions
        ChatOptions = new ChatOptions { Instructions = "Be brief.", Tools = [ShippingTool] },
        DisableTodoProvider = true,
        DisableAgentModeProvider = true,
        DisableAgentSkillsProvider = true,
        DisableWebSearch = true,
        DisableFileMemory = true,
        DisableToolAutoApproval = false,   // keep the auto-approval rules; approval-required tools stop either way
        MaximumIterationsPerRequest = 5,
    };
}
