# Contoso v7: the qualified version

v6 as it is, frozen as a candidate that a team who did not build it can
identify, run and check. Nothing in v6, v5, v4, v3 or v2 changes.

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

## The campaign

`src/Ch23.Qualification/Ch23.QualificationRunner` reads `release/candidate.json`
and writes `qualification-results.json`; `--dossier` renders
`release-dossier.md` from it, the failure journal and the capability file.

```bash
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --offline --out <dir>
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --real --budget-minutes 20 --into <dir>/qualification-results.json --out <dir>
dotnet run --project src/Ch23.Qualification/Ch23.QualificationRunner -- --dossier <dir>/qualification-results.json
```

## The handoff drill

`--drill` runs four situations on a fresh store and prints the business state
after each: a conversation restored after the API process is lost; a valid id
from another tenant (404 on the conversation and on the claim); an approval of
what the reviewer saw before the account changed (409 stale_version, the claim
still waits); a worker killed after the provider accepted a payment (the claim
shows OutcomeUnknown; a new worker reconciles by reference, one payment).

```bash
dotnet run --project contoso/v7-qualified -- --self-test   # gate, refusals, drill; RESULT n/n
dotnet run --project contoso/v7-qualified -- --drill
```

The drill is scripted. Having an engineer who did not build Contoso run it from
this folder alone, and recording what they could not explain, is the matrix's
human row H1; the runner never marks it.

## What this snapshot does not do

The `limits` list in `release/candidate.json` is the source; the dossier prints it.
In short: simulated payments only; the gate is on the HTTP API, not on v5's
command line; the real-provider layer is one small local model, one run; no cloud
provider was run; the host agent's free-text answers are not evaluated; one machine.

## Running

```bash
dotnet run --project contoso/v5-durable-resume -- prepare
cd contoso/v7-qualified
dotnet user-jwts create --name rita --claim tid=contoso --output token
dotnet run
curl http://localhost:5000/release -H "Authorization: Bearer <token>"
```

The build output's `appsettings.json` is v5's, as in v6.
