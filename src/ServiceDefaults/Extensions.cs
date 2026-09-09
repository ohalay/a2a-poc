using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Standard Aspire "service defaults": OpenTelemetry (traces + metrics + logs),
/// default health checks, HTTP resilience, and service discovery. Calling
/// <see cref="AddServiceDefaults"/> from each service's Program.cs makes the
/// service emit OTLP telemetry that the Aspire dashboard renders as traces.
/// </summary>
public static class Extensions
{
    // Any custom ActivitySource whose name starts with this prefix is captured
    // and exported, so app-defined spans (e.g. the orchestration pipeline) show
    // up as traces in the dashboard.
    public const string ActivitySourceNamePrefix = "A2A";

    // Microsoft.Extensions.AI's OpenTelemetryChatClient (added by
    // .UseOpenTelemetry()) emits the LLM chat span under this ActivitySource
    // name. The span is named "chat <model>" (e.g. "chat llama3.2") with
    // gen_ai.* tags, so you can see requests going to the Ollama model.
    public const string ExtensionsAiSourceName = "Experimental.Microsoft.Extensions.AI";

    // FunctionInvokingChatClient emits the PER-TOOL-CALL span (the "execute_tool"
    // span that wraps each agent dispatch) under its OWN ActivitySource name --
    // NOT the "Experimental.Microsoft.Extensions.AI" source above. In
    // Microsoft.Extensions.AI 10.9.0 the tool-invocation span is owned by the
    // function-invoking client itself, independent of the OpenTelemetry chat
    // decorator, so it must be registered explicitly or the tool spans are
    // dropped (which is why they never appeared under "chat llama3.2").
    public const string FunctionInvocationSourceName = "Microsoft.Extensions.AI.FunctionInvokingChatClient";

    // The A2A SDK (client + server transport) emits its own spans under this name.
    public const string A2ASdkSourceName = "A2A";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                       .AddHttpClientInstrumentation()
                       .AddRuntimeInstrumentation()
                       .AddMeter(ExtensionsAiSourceName);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                       .AddAspNetCoreInstrumentation()
                       .AddHttpClientInstrumentation()
                       // "A2A*" wildcard covers the A2A SDK ("A2A", "A2A.AspNetCore")
                       // plus our own custom sources ("A2A.Orchestrator",
                       // "A2A.Agent.Assortment", "A2A.Agent.SupplyChain").
                       .AddSource(ActivitySourceNamePrefix + "*")
                       // Microsoft.Extensions.AI chat span ("chat <model>").
                       .AddSource(ExtensionsAiSourceName)
                       // Microsoft.Extensions.AI per-tool-call span
                       // ("execute_tool <agent>"), from FunctionInvokingChatClient.
                       .AddSource(FunctionInvocationSourceName);
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks("/health");
            app.MapHealthChecks("/alive", new()
            {
                Predicate = r => r.Tags.Contains("live"),
            });
        }

        return app;
    }
}
