# Exercise 03 — Containerize, Secure, and Deploy (Challenge)

## Prompt

Containerize an agent API that already has bearer authentication and a rate
limit partitioned by tenant and subject. Keep every secret out of the image:
the provider key and the token settings arrive at run time.

## Accepted when

- the image runs as a non-root user;
- `docker history` shows no secret;
- two tokens from one IP are limited independently;
- a wrong-audience token returns 401.

The first two are checked by `check-image.ps1`, the last two by the
self-test, which the script also runs inside the image.

## Run

```bash
dotnet run --project exercises/Ch17.Exercises/Ex03.ContainerizedDeploy -- --self-test
pwsh exercises/Ch17.Exercises/Ex03.ContainerizedDeploy/check-image.ps1
```

`check-image.ps1` publishes the project, builds the image from the published
folder with `--pull=false --network none` (so it needs the local base image
`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`), checks the user and
the layer history, and runs `--self-test` in the container with no network.
To prove the history check can fail, it first plants a key in an `ENV` layer
of a throwaway image and looks for it.

## The default path: SDK container publishing

```bash
dotnet publish -c Release --os linux --arch x64 /t:PublishContainer
docker run -p 8080:8080 -e LlmProvider__Provider=ollama ch17-ex03-agent-api:1.0.0
```

The `<Container*>` properties in the `.csproj` shape that image (chiseled
base, OCI format). SDK publishing pulls the base image from the registry, so
it needs network access; the book's checks use the Dockerfile path instead.

## Deploying to the cloud (not run in the book)

From the Aspire AppHost
(`src/Ch17.HostingIdentity/Ch17.AspireOrchestration/Ch17.AspireOrchestration.AppHost`),
the Azure Developer CLI packages and deploys the app (`azd init`, `azd up`).
That path needs an Azure subscription; it was not run for the book.
