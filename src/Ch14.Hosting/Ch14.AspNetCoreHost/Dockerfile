# ============================================================================
# Chapter 13 — multi-stage Dockerfile for Ch13.AspNetCoreHost
#
# The DEFAULT path in the chapter is SDK container publishing (no Dockerfile):
#   dotnet publish -c Release --os linux --arch x64 /t:PublishContainer
#
# This Dockerfile is the CUSTOM path, for teams that require one in source
# control. Build context is the CodeSample repo root (it references the
# Common project), so build from there:
#   docker build -f src/Ch13.Hosting/Ch13.AspNetCoreHost/Dockerfile -t ch13-agent-api .
# ============================================================================

# ---- Build stage: full SDK ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Central package management files first, for layer-cached restores.
COPY Directory.Packages.props Directory.Build.props ./
COPY src/Common/AgenticAI.Common/AgenticAI.Common.csproj src/Common/AgenticAI.Common/
COPY src/Ch13.Hosting/Ch13.AspNetCoreHost/Ch13.AspNetCoreHost.csproj src/Ch13.Hosting/Ch13.AspNetCoreHost/
RUN dotnet restore src/Ch13.Hosting/Ch13.AspNetCoreHost/Ch13.AspNetCoreHost.csproj

# Copy the rest and publish.
COPY src/ src/
RUN dotnet publish src/Ch13.Hosting/Ch13.AspNetCoreHost/Ch13.AspNetCoreHost.csproj \
    -c Release -o /app --no-restore

# ---- Runtime stage: chiseled (no shell, no package manager, non-root) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .

# Chiseled images already run as the non-root 'app' user (UID 1654).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ch13.AspNetCoreHost.dll"]
