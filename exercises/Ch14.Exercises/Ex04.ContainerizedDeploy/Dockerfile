# ============================================================================
# Exercise 04 — multi-stage Dockerfile (the CUSTOM path)
#
# The DEFAULT path in the chapter needs no Dockerfile at all:
#   dotnet publish -c Release --os linux --arch x64 /t:PublishContainer
#
# This Dockerfile is for teams that require one in source control. The build
# context is the CodeSample repo root (it relies on Directory.Packages.props /
# Directory.Build.props), so build from there:
#   docker build -f exercises/Ch13.Exercises/Ex04.ContainerizedDeploy/Dockerfile \
#       -t ch14-ex04-agent-api .
# ============================================================================

# ---- Build stage: full SDK ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Central package-management files first, for layer-cached restores.
COPY Directory.Packages.props Directory.Build.props ./
COPY exercises/Ch13.Exercises/Ex04.ContainerizedDeploy/Ex04.ContainerizedDeploy.csproj exercises/Ch13.Exercises/Ex04.ContainerizedDeploy/
RUN dotnet restore exercises/Ch13.Exercises/Ex04.ContainerizedDeploy/Ex04.ContainerizedDeploy.csproj

# Copy the rest and publish.
COPY exercises/Ch13.Exercises/Ex04.ContainerizedDeploy/ exercises/Ch13.Exercises/Ex04.ContainerizedDeploy/
RUN dotnet publish exercises/Ch13.Exercises/Ex04.ContainerizedDeploy/Ex04.ContainerizedDeploy.csproj \
    -c Release -o /app --no-restore

# ---- Runtime stage: chiseled (no shell, no package manager, non-root) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .

# Chiseled images already run as the non-root 'app' user. NEVER bake secrets
# into a layer — inject the LLM provider key at runtime (-e / Key Vault / MI).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ex04.ContainerizedDeploy.dll"]
