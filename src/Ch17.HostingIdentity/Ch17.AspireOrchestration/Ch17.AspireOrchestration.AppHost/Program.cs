// ============================================================================
// Aspire AppHost: starts Ch17.AspNetCoreHost as the "agent-api" resource and
// opens the Aspire dashboard (traces, logs, metrics over OTLP).
//
// The dashboard marks agent-api healthy only when /health/ready answers 200.
// That probe checks the conversation store and never calls the model; the
// liveness probe (/health/live) does not either, so a health poll costs no tokens.
//
// Build only in the book's checks: running it needs the Aspire dashboard and
// DCP binaries the Aspire SDK restores per operating system.
// ============================================================================

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Ch17_AspNetCoreHost>("agent-api")
    .WithHttpHealthCheck("/health/ready")
    .WithExternalHttpEndpoints();

builder.Build().Run();
