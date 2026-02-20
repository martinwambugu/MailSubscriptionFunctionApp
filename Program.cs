using Azure.Monitor.OpenTelemetry.Exporter;
using MailSubscriptionFunctionApp.Infrastructure;
using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Middleware;
using MailSubscriptionFunctionApp.Services;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker.Extensions.OpenApi.Extensions;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System;
using System.IO;
using System.Net;
using System.Net.Http;

// ✅ CRITICAL: Force TLS 1.2+ BEFORE any HTTP calls  
// This must be the FIRST executable code in the application  
ServicePointManager.SecurityProtocol =
    SecurityProtocolType.Tls12 |
    SecurityProtocolType.Tls13;

// ✅ Configure default HTTP connection settings  
ServicePointManager.DefaultConnectionLimit = 100;
ServicePointManager.Expect100Continue = false;
ServicePointManager.CheckCertificateRevocationList = false;

// ✅ Set .NET Core environment variables for HTTP behavior  
Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER", "0");
Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP2SUPPORT", "1");

// -----------------------------------------------------  
// Load Configuration  
// -----------------------------------------------------  
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

string telemetryBackend = configuration["TELEMETRY_BACKEND"]?.ToLowerInvariant() ?? "azure";

// -----------------------------------------------------  
// Build Host  
// -----------------------------------------------------  
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication((context, builder) =>
    {
        // -----------------------------------------------------  
        // CORS CONFIGURATION ✅  
        // -----------------------------------------------------  
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy
                    .AllowAnyOrigin()  // ⚠️ Restrict in production  
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });

        // -----------------------------------------------------  
        // Logging Pipeline with OpenTelemetry  
        // -----------------------------------------------------  
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole(); // ✅ Required for container/K8s logs  

            logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.ParseStateValues = true;
                options.IncludeFormattedMessage = true;

                if (telemetryBackend == "onprem")
                {
                    options.AddOtlpExporter(otlp =>
                    {
                        otlp.Endpoint = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4317");
                    });
                }
                else if (telemetryBackend == "azure")
                {
                    options.AddAzureMonitorLogExporter(o =>
                    {
                        o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                    });
                }
            });
        });

        // -----------------------------------------------------  
        // Metrics & Tracing with OpenTelemetry  
        // -----------------------------------------------------  
        var otel = builder.Services.AddOpenTelemetry();

        otel.WithMetrics(metrics =>
        {
            metrics
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation()
                .AddAspNetCoreInstrumentation();

            if (telemetryBackend == "onprem" &&
                bool.TryParse(configuration["PROMETHEUS_ENABLED"], out var prometheusEnabled) &&
                prometheusEnabled)
            {
                metrics.AddPrometheusHttpListener(options =>
                {
                    options.UriPrefixes = new string[] { "http://+:9464/" };
                });
            }
            else if (telemetryBackend == "azure")
            {
                metrics.AddAzureMonitorMetricExporter(o =>
                {
                    o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                });
            }
        });

        otel.WithTracing(traces =>
        {
            traces
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .SetSampler(new AlwaysOnSampler());

            if (telemetryBackend == "onprem")
            {
                traces.AddOtlpExporter(otlp =>
                {
                    otlp.Endpoint = new Uri(configuration["OTLP_ENDPOINT"] ?? "http://localhost:4317");
                });
            }
            else if (telemetryBackend == "azure")
            {
                traces.AddAzureMonitorTraceExporter(o =>
                {
                    o.ConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                });
            }
        });

        // -----------------------------------------------------  
        // Telemetry Abstraction (ICustomTelemetry)  
        // -----------------------------------------------------  
        if (telemetryBackend == "azure")
        {
            builder.Services.AddApplicationInsightsTelemetryWorkerService();
            builder.Services.AddSingleton<ICustomTelemetry, AzureAppInsightsTelemetry>();
            builder.Services.AddSingleton<TelemetryClient>();
            builder.Services.AddSingleton<ITelemetryInitializer, OperationCorrelationTelemetryInitializer>();
        }
        else
        {
            builder.Services.AddSingleton<ICustomTelemetry, NoOpTelemetry>();
        }

        // -----------------------------------------------------  
        // Infrastructure Layer Registrations  
        // -----------------------------------------------------  
        builder.Services.AddScoped<IDbConnectionFactory, DbConnectionFactory>();

        // -----------------------------------------------------  
        // Domain Service Registrations  
        // -----------------------------------------------------  
        builder.Services.AddScoped<IGraphSubscriptionClient, GraphSubscriptionService>();
        builder.Services.AddScoped<IMailSubscriptionRepository, MailSubscriptionService>();

        // ✅ Configure HttpClient factory with proper TLS settings for Graph API  
        builder.Services.AddHttpClient("GraphClient", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(100);
            client.DefaultRequestHeaders.Add("User-Agent", "MailSubscriptionFunctionApp/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // ✅ Force TLS 1.2 and TLS 1.3  
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                          System.Security.Authentication.SslProtocols.Tls13,

            // ✅ Production: Use default certificate validation  
            // For testing only: uncomment line below (NOT RECOMMENDED for production)  
            // ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,  

            MaxConnectionsPerServer = 100,
            UseProxy = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                    System.Net.DecompressionMethods.Deflate
        })
        .AddStandardResilienceHandler(options =>
        {
            // ✅ Configure retry policy  
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
            options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;

            // ✅ Configure circuit breaker  
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(90);
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.MinimumThroughput = 10;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);

            // ✅ Configure timeout  
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(100);
        });

        // ✅ Add default HttpClient for other services  
        builder.Services.AddHttpClient();

        // -----------------------------------------------------  
        // Middleware Pipeline  
        // -----------------------------------------------------  
        builder.Services.AddCustomMiddlewares(configuration);
        builder.UseCustomMiddlewares(configuration);

        // -----------------------------------------------------  
        // OpenAPI / Swagger  
        // -----------------------------------------------------  
        builder.Services.AddSingleton<IOpenApiConfigurationOptions, OpenApiConfig>();
    })
    // ✅ Ensure CORS middleware is active in Functions runtime  
    .ConfigureServices(services =>
    {
        services.AddCors();

        // ✅ Add MemoryCache for GraphServiceClient caching  
        services.AddMemoryCache();
    })
    .ConfigureOpenApi()
    .Build();

// -----------------------------------------------------  
// Run Host  
// -----------------------------------------------------  
host.Run();