# Exercise 01 — Describe an Image: DataContent vs UriContent (Basic)

## Prompt

Send a local image to a vision agent with `DataContent` and print the description. Then add a second run that sends the same question with the image as a `UriContent` URL, and compare what each path returns on your configured model. The lesson is in the difference.

## What it does

- **Run 1 — local bytes**: loads the image from disk and attaches it as a `DataContent` part (the bytes are inlined into the request). Works on every vision provider, including local Ollama `llava`. Default image: the clean taxi receipt from `assets/receipts`.
- **Run 2 — hosted URL**: attaches an image URL as a `UriContent` part. The provider must fetch it. The Ollama connector (OllamaSharp 5.4.25) drops the part without an error, so a local model answers the question without seeing any image. The program prints that note next to the answer instead of passing the answer off as a description.
- Both runs use the same question, are timed, and are printed side by side.

For a fair comparison on a cloud provider, pass the URL of the same image you pass as a file: `dotnet run -- photo.png https://.../photo.png`.

## Acceptance

- Both outputs are shown.
- The URL run's behavior on a local model is explained in the output, not hidden.
- `dotnet run -- --self-test` passes offline: it sends both runs through the real Ollama connector into a recording handler and checks that run 1 carries the bytes and run 2 carries neither an image nor the URL.

## Run

```
dotnet run -- [image path] [image URL]
dotnet run -- --self-test
```

Configure a vision model under `VisionModel` in `appsettings.json` (Ollama `llava`, or OpenAI).
