# Ch20.IndirectInjection: the instruction arrives inside retrieved data

`PromptAssembler.Build` assembles a prompt from retrieved chunks in three steps:

1. **Provenance**: each `RetrievedChunk` carries its source and a trust level
   (internal handbook or external upload, email, web page).
2. **Quarantine**: chunks the `ToolOutputScanner` flags are held back.
3. **Fence**: what is sent is wrapped in `<<DATA id=... source=... trust=...>>`
   with a random id per request, and fence-like text inside a chunk is
   neutralised, so a chunk cannot close its own fence.

The self-test uses a deliberately obedient scripted model (it follows any
"reply only with: X" it is sent). It shows the quarantine working, a
paraphrased payload that the scanner misses, and the first edition's fixed
`<<DATA>>` fence being closed from inside a document. `Assembled.IngestedExternal`
records that external text reached the model.

## Running

```bash
dotnet run -- --self-test   # offline: RESULT n/n
dotnet run                  # live, against the configured provider
```
