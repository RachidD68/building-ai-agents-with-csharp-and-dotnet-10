using System.ComponentModel;
using Ch08.ActionPolicy;
using ModelContextProtocol.Server;

namespace Ch20.PolicyEnforcement;

// Shapes kept only to reproduce their defects. Never wire them into a host.

/// <summary>
/// The check lived in the HTTP endpoint; the MCP tool added later called the
/// ledger directly. Every check the API makes is skipped on this path.
/// </summary>
[McpServerToolType]
public sealed class UnguardedMcpTools(AccountStore accounts)
{
    [McpServerTool(Name = ReimbursementService.Tool), Description("Submit a reimbursement for an employee.")]
    public string Reimburse(string employeeId, decimal amount, string currency, string operationId)
    {
        accounts.DebitUnchecked(employeeId, amount);
        return "Allow posted";
    }
}

/// <summary>Checks the balance, awaits something, then debits: two claims can pass the same check.</summary>
public static class CheckThenAct
{
    public static async Task<bool> ReimburseAsync(AccountStore accounts, Caller caller, ReimbursementCommand command,
        Func<Task> between, DateTimeOffset now)
    {
        EmployeeAccount account = accounts.Get(command.EmployeeId);
        ReimbursementProposal proposal = new(command.OperationId, command.EmployeeId, command.Amount, command.Currency, account.Version);
        if (ReimbursementPolicy.Evaluate(caller, account, proposal, null, now).Decision != Decision.Allow) return false;
        await between();                          // a database round trip, a log write, an HTTP call...
        accounts.DebitUnchecked(command.EmployeeId, command.Amount);
        return true;
    }
}
