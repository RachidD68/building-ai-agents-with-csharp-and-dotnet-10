# Contoso v7: the qualified version

v6 as it is, frozen as a candidate that a team who did not build it can
identify, run and check, plus the assistant configuration its live layer
evaluates. Nothing in v6, v5, v4 or v2 changes; v3's project file gains one line
(`InternalsVisibleTo`) so v7 can run v3's live scenarios as they are.

```
release/candidate.json        the candidate manifest (no secrets): projects, compiled prompts,
                              skills, data versions, model profile, campaign layers,
                              acceptance matrix, known limitations
release/capabilities.json     what this release ships; every disabled capability has a reason
release/failure-journal.json  each finding of the campaign, its disposition, the decision record

GET /release                  candidate, profile, capability states (authenticated)
gate                          a disabled capability's routes: 503 {"refused":"capability_disabled",
                              "capability":..., "reason":...}, after authentication
route check                   a mapped route that belongs to no capability, or a declared route
                              that is not mapped, stops the host at start
```

The release profile (`Release:Profile=release`, the default) ships
`claims.read`, `conversations` and `operations`, and ships `claims.decide`
(`POST /claims/{id}/decision` and `/approval`) **disabled**: an approved claim
is paid through the simulated `FakePaymentProvider`, and no real payment
provider is integrated or qualified. The `qualification` profile turns every
capability on; the campaign and the drill use it, with simulated effects only.

## The assistant the live layer evaluates

```
RequesterAssistant.ToolsFor   tools per caller: submit_for_review for a requester;
                              approve_reimbursement only for a finance reviewer, and
                              only while claims.decide ships enabled
ClaimActions                  the effects behind the tools (simulated); rights re-read
                              from the directory, capability, separation of duties
AnswerContractClient          after generation: every stated value carries a supplied
                              evidence id; a value sentence with none gets the id of the
                              one supplied chunk that holds all its numbers; otherwise
                              one retry without tools; still unbacked -> the desk's
                              abstention (or, on an action turn, the tool's result)
--evidence-live               the requester assistant on evidence-cases.v1, scored with
                              the evaluation chapter's five layers (linked files)
--faq-live                    v3's two FAQ desks, unchanged, on v3's live scenarios,
                              behind the contract
```

## The campaign

`src/Ch23.Qualification/Ch23.QualificationRunner` reads `release/candidate.json`
and writes `qualification-results.json`; `--dossier` renders
`release-dossier.md` from it, the failure journal and the capability file.

```bash
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --offline --out <dir>
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --real --budget-minutes 20 --into <dir>/qualification-results.json --out <dir>
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --dossier <dir>/qualification-results.json
```

The release folder names candidate `contoso-v7.2`. The first candidate,
`contoso-v7`, stopped when the evidence scorer crashed (journal FJ-01, fixed by
956c463). On `contoso-v7.1` three rows failed, F6, F9 and C8: its live evidence
step evaluated the evaluation chapter's demonstration agent, which offers the
approve tool to every caller and checks no citation. `contoso-v7.2` (fixes
78bc373, manifest 81bc32b) evaluates the configuration above: every row that
applies passed, F10 (Azure) was not run, and H1 and H2 wait for people. The nine
value answers were cited by the contract's attachment, not by the model (FJ-03).
That supports calling `contoso-v7.2` qualified within the limits below, no
further. The runs of record are `docs/outputs/Ch23.release-dossier.md`,
`Ch23.contoso-v7.1.release-dossier.md` and `Ch23.contoso-v7.release-dossier.md`.

## The handoff drill

`--drill` runs four situations on a fresh store and prints the business state
after each: a conversation restored after the API process is lost; a valid id
from another tenant (404 on the conversation and on the claim); an approval of
what the reviewer saw before the account changed (409 stale_version, the claim
still waits); a worker killed after the provider accepted a payment (the claim
shows OutcomeUnknown; a new worker reconciles by reference, one payment).

```bash
dotnet run --project contoso/v7-qualified -- --self-test   # gate, refusals, drill, tools, contract; RESULT n/n
dotnet run --project contoso/v7-qualified -- --drill
dotnet run --project contoso/v7-qualified -- --evidence-live   # Ollama
dotnet run --project contoso/v7-qualified -- --faq-live        # Ollama + nomic-embed-text
```

The drill is scripted. Having an engineer who did not build Contoso run it from
this folder alone, and recording what they could not explain, is the matrix's
human row H1; the runner never marks it.

## What this snapshot does not do

The `limits` list in `release/candidate.json` is the source; the dossier prints it.
In short: simulated payments only; the gate is on the HTTP API, not on v5's
command line; the real-provider layer is one small local model, one run; no cloud
provider was run; the host agent's free-text answers are not evaluated (the
requester assistant and v3's desks are console entry points no route serves); the
contract proves where a cited number came from, not that the sentence is supported;
in-memory indexes; one machine.

## Running

```bash
dotnet run --project contoso/v5-durable-resume -- prepare
cd contoso/v7-qualified
dotnet user-jwts create --name rita --claim tid=contoso --output token
dotnet run
curl http://localhost:5000/release -H "Authorization: Bearer <token>"
```

The build output's `appsettings.json` is v7's own: v3's `LlmProvider` section and
v5's `VisionModel` section.
