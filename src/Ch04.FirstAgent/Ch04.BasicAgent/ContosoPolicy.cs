using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch04.FirstAgent;

/// <summary>
/// Contoso's expense-policy assistant: answers questions, takes no action.
/// The facts are the ones the later Contoso snapshots enforce in code
/// (50.00 EUR low-value limit, a finance reviewer who is not the requester,
/// one payment per claim); here the model only explains them.
/// </summary>
public static class ContosoPolicy
{
    public const string Name = "ContosoExpenseHelp";
    public const string Description = "Answers Contoso employees' questions about the expense policy";

    public const string Instructions = """
        You answer questions from Contoso employees about the expense policy.
        Policy facts:
        - Claims are reimbursed in EUR.
        - A claim of 50.00 EUR or less is approved automatically.
        - A claim above 50.00 EUR waits for a finance reviewer, who cannot be the employee who filed it.
        - Each expense is reimbursed once; submitting the same claim twice does not pay it twice.
        You cannot file, approve or pay a claim. If asked to, say so and point to the expense form.
        If these facts do not answer the question, say that you don't know.
        Answer in at most three sentences.
        """;

    /// <summary>The agent every Contoso v0 host uses: one client, the policy instructions.</summary>
    public static ChatClientAgent Create(IChatClient chatClient) =>
        new(chatClient, instructions: Instructions, name: Name, description: Description);
}
