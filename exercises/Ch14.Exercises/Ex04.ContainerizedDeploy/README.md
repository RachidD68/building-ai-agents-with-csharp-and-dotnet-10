# Exercise 04 — Containerize, Secure, and Deploy (Challenge)

## Prompt

Take a hosted agent API all the way to a cloud deployment: protect it with JWT
bearer authentication and a per-user rate limit, publish it as a container, and
deploy it to Azure Container Apps with `azd up`. Confirm the deployed endpoint
returns **401** without a token and **200** with one.

This project is the **runnable base**. The auth, rate limiting, and SSE streaming
are already wired (see `Program.cs`); the exercise is the containerize-and-deploy
arc. The worked solution is in **Appendix E**.

## Key Concepts

- JWT bearer authentication (`AddJwtBearer` + `RequireAuthorization`)
- Per-user rate limiting (`AddRateLimiter` + `RequireRateLimiting`)
- SSE streaming (`TypedResults.ServerSentEvents`)
- SDK container publishing (`dotnet publish /t:PublishContainer`, no Dockerfile)
- Deployment to Azure Container Apps with the Azure Developer CLI (`azd`)

## Run locally

```bash
# Default provider is Ollama (no cloud config). Start the API:
dotnet run

# Mint a local dev token (no identity provider needed) and call /chat:
TOKEN=$(dotnet user-jwts create --output token)
curl -i -X POST http://localhost:5000/chat \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"message":"What is dependency injection?"}'

# Without the token you get 401:
curl -i -X POST http://localhost:5000/chat \
  -H "Content-Type: application/json" -d '{"message":"hi"}'

# Stream tokens (note -N to disable buffering):
curl -N -X POST http://localhost:5000/chat/stream \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"message":"Name three benefits of DI."}'
```

## 1. Containerize

The default path needs **no Dockerfile** — the .NET 10 SDK builds the image:

```bash
dotnet publish -c Release --os linux --arch x64 /t:PublishContainer
docker run -p 8080:8080 -e LlmProvider__Provider=ollama ch14-ex04-agent-api:1.0.0
```

The image shape is set by the `<Container*>` properties in the `.csproj`
(chiseled base, OCI format, non-root). **Never** bake the provider key into a
layer — pass it at runtime with `-e`, a mounted secret, or a managed identity.

A multi-stage `Dockerfile` is included for teams that require one in source
control (build from the CodeSample repo root — see the file header).

## 2. Deploy to Azure Container Apps

From the Aspire AppHost (`src/Ch14.Hosting/Ch14.AspireOrchestration/Ch14.AspireOrchestration.AppHost`):

```bash
azd auth login          # authenticate once
azd init                # detects the Aspire AppHost, generates azure.yaml
azd up                  # package + provision + deploy
# ... iterate ...
azd deploy agent-api    # redeploy just the code (no re-provision)
azd down                # tear it all down when finished
```

`azd up` builds the container (using the same SDK publishing as above),
provisions an Azure Container Registry and a Container Apps environment, and
deploys the app — printing a public `*.azurecontainerapps.io` URL.

## Acceptance

- `/chat` returns **401** without a bearer token, **200** with one (locally and
  on the deployed endpoint).
- The published image is chiseled and runs as a non-root user.
- No secret appears in any image layer.
- `azd up` produces a reachable `*.azurecontainerapps.io` endpoint.
