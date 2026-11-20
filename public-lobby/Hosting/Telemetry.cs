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
// generated ServiceDefaults project.
//
// ALL of it is gated on OTEL_EXPORTER_OTLP_ENDPOINT, not just the exporter. Production (Railway)
// never sets it, and with the pipelines registered anyway every request still built Activities and
// fed metric instruments that nothing ever read — CPU, allocations, and (because the lobby's memory
// is mostly JIT code and runtime data, not managed heap) resident memory for the OpenTelemetry SDK
// paths themselves. Aspire injects the variable for every resource it runs, so the dashboard view
// is unchanged; set it by hand to get telemetry from any other run.
static class Telemetry
{
    public static WebApplicationBuilder AddLobbyTelemetry(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            return builder;

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
            )
            .UseOtlpExporter();

        return builder;
    }
}
