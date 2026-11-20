# Contoso v4: receipts

The Contoso expense assistant at the end of the multimodal-documents chapter. The employee photographs a receipt instead of typing a claim.

```
receipt pages -> vision reading -> validation after extraction
  -> ready   -> v2 ReimbursementDesk (policy, review, commit, payment: unchanged)
  -> blocked -> AwaitingVerification -> finance reviewer (not the requester) types the total
             -> v2 ReimbursementDesk
```

`ReceiptIntake` sits in front of the v2 desk, which it uses as it is (project reference to `contoso/v2-authorized-reimbursement`). The reading and validation are `Ch12.ReceiptExtraction`'s files, compiled in as links. The v2 desk asks an `IChatClient` for its `ProposalDraft`; for receipts that client is `VerifiedProposals`, which answers with the validated values, or the values a reviewer typed, for that submission. No model writes the amount the policy judges.

The scenario: Alice photographs her taxi receipt and double-clicks Submit (two concurrent calls, one reading, one claim, AwaitingReview); she submits the same photo as a new claim (refused, `duplicate_receipt`); a faint receipt with an unreadable total waits for a person; Alice, who also holds the finance-reviewer role, cannot verify her own receipt, Bob has no role, a reviewer of another tenant cannot see it; Rita types 64.80 EUR and the policy decides; a receipt with an instruction printed on it waits for a person; a two-page hotel invoice goes through with the total from page 2; Bob's receipt with two totals waits, his right to submit is revoked, and Rita's verification ends Rejected (`missing_permission`) at the desk; Rita approves the taxi claim and v2 pays it once.

Intake states: `AwaitingVerification`, `Rejected` (`duplicate_receipt`, `reading_failed`), `Refused` (a verification that is not allowed), and then the v2 claim states.

## What this snapshot does not do

- Receipts waiting for a person live in memory. A restart loses them (the v2 claims after the hand-off are in SQLite). A real intake stores the pending receipt and its reading the way v2 stores claims.
- Duplicate detection compares the exact image bytes. A second photo of the same paper receipt is a different image; catching it needs the receipt number and vendor, which the reading would have to report reliably first.
- The FAQ-with-evidence snapshot (v3) is not in the repository yet. v4 builds on v2; when v3 lands, its answer path sits beside this intake and does not change it.

## Running

```bash
dotnet run --project contoso/v4-receipts                  # Ollama llava by default (VisionModel section)
dotnet run --project contoso/v4-receipts -- --self-test   # scripted vision replies, no network
```

The receipts are the synthetic corpus in `assets/receipts` (CC0). Simulated effects only: the payment provider is v2's fake with its own journal file, and both databases live in a temporary folder that is deleted at the end.
