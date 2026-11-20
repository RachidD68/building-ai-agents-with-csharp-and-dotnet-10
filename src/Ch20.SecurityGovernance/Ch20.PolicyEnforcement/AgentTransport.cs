using System.ComponentModel;
using Ch08.ActionPolicy;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Ch20.PolicyEnforcement;

// ── Agent hosts (A2A, AG-UI) ───────────────────────────────────────────────

/// <summary>
/// An A2A or AG-UI host ends in an agent run whose tool performs the effect.
/// The host builds the Caller from its own auth context per request and
/// creates the tool for that caller; the model never supplies it.
/// </summary>
public static class AgentTransport
{
    public static ChatClientAgent Create(IChatClient model, ReimbursementService service, Caller caller, string transport)
    {
        [Description("Submit a reimbursement for an employee.")]
        string Reimburse(string employeeId, decimal amount, string currency, string operationId) =>
            Guid.TryParse(operationId, out Guid id)
                ? service.Reimburse(transport, caller, new ReimbursementCommand(id, employeeId, amount, currency)).ToString()
                : "Deny missing_operation_id";

        return new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = "ReimbursementAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You help employees with reimbursements. Use the reimburse tool.",
                Tools = [AIFunctionFactory.Create(Reimburse, name: ReimbursementService.Tool)],
            },
        });
    }
}
