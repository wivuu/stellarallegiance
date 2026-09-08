using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace PublicLobby.Hosting;

// OpenTelemetry wiring (logs + metrics + traces), OTLP-exported. This exists so an Aspire AppHost
// running the lobby locally can show its telemetry in the Aspire dashboard alongside the rest of
// the distributed app — mirrors the pattern Aspire's ServiceDefaults template generates
// (ConfigureOpenTelemetry), reimplemented by hand here since public-lobby doesn't reference a
// generated ServiceDefaults project. Production (Railway) never sets OTEL_EXPORTER_OTLP_ENDPOINT,
// so the exporter is a no-op there — this is a local dev/observability aid, not a hosting change.
static class Telemetry
{
    public static WebApplicationBuilder AddLobbyTelemetry(this WebApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(l =>
        {
            l.IncludeFormattedMessage = true;
            l.IncludeScopes = true;
        });

        builder
            .Services.AddOpenTelemetry()
            .WithMetrics(m =>
                m.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Orleans 10 publishes its meters (silo/grain/messaging stats) under this name.
                    .AddMeter("Microsoft.Orleans")
            )
            .WithTracing(t =>
                t.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(o =>
                        // Health checks are polled constantly (PaaS liveness probe) and would
                        // otherwise dominate the trace view with nothing interesting to show.
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")
                    )
                    .AddHttpClientInstrumentation()
            );

        // Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT (pointed at its dashboard's collector) for
        // every resource it runs locally; only add the OTLP exporter when that's set so a
        // non-Aspire run (Railway, docker-compose, `dotnet run` standalone) doesn't try to dial
        // an endpoint that doesn't exist.
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
    }
}
