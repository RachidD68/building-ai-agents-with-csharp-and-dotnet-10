---
name: expense-report
description: |
  File and validate Contoso employee expense reports against company spend
  policy. Use when asked about expense submissions, reimbursement rules,
  spending limits, or whether a specific expense category is allowed.
license: Apache-2.0
metadata:
  author: contoso-finance
  version: "1.0"
---

# Expense report workflow

When the user asks about expenses, reimbursements, or spending policy:

1. Read the `references/POLICY_FAQ.md` resource to ground your answer in the
   current Contoso spend policy. Quote the relevant section heading in your reply.
2. If the user asks whether a specific line item is allowed, run the `validate`
   script to get a deterministic verdict, then quote the rule it returns. Pass
   the arguments as a JSON array of strings: the category, the amount in US
   dollars, and optionally the trip scope. For example, to check a USD 90 client
   dinner, call run_skill_script with `["meals", "90", "domestic"]`. Valid
   categories are: meals, lodging, ground, air, conference, alcohol, other.
3. If the policy does not cover the user's question, say so plainly. Do not
   invent rules.
4. If the user asks for a draft expense report, return a markdown table with
   the columns Date, Vendor, Category, Amount, Justification. Match the
   categories listed in the policy.

Be concise. Two short paragraphs is usually the right length.
