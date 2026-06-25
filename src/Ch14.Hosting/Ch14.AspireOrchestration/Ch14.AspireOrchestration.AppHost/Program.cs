// ============================================================================
// Chapter 13 — Aspire Orchestration (AppHost)
// The Aspire AppHost orchestrates the multi-agent API and provides:
//   - Service discovery between projects
//   - Centralized configuration and environment management
//   - Built-in dashboard for monitoring and tracing
//
// NOTE: Requires the .NET Aspire workload or the Aspire.Hosting.AppHost
// NuGet package. See README.md for setup instructions.
// ============================================================================

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// ---------------------------------------------------------------
// Register the ASP.NET Core agent host as a project resource
// ---------------------------------------------------------------
IResourceBuilder<ProjectResource> agentApi = builder.AddProject<Projects.Ch14_AspNetCoreHost>("agent-api")
    .WithExternalHttpEndpoints();

builder.Build().Run();
