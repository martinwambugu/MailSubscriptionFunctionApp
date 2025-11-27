using MailSubscriptionFunctionApp.Infrastructure;
using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Middleware;
using MailSubscriptionFunctionApp.Services;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;

var builder = FunctionsApplication.CreateBuilder(args);

// Configuration  
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Services registration  
builder.Services.AddScoped<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddScoped<ICustomTelemetry, AzureAppInsightsTelemetry>();
builder.Services.AddScoped<IGraphSubscriptionClient, GraphSubscriptionService>();
builder.Services.AddScoped<IMailSubscriptionRepository, MailSubscriptionService>();

// Telemetry  
builder.Services.AddSingleton<TelemetryClient>();
builder.Services.AddSingleton<ITelemetryInitializer, OperationCorrelationTelemetryInitializer>();

// Middleware registration  
builder.Services.AddCustomMiddlewares(builder.Configuration);

// Logging  
builder.Services.AddLogging(lb =>
{
    lb.AddConsole();
    lb.AddApplicationInsights();
});

// Apply middlewares  
builder.ConfigureFunctionsWebApplication(app =>
{
    app.UseCustomMiddlewares(builder.Configuration);
});

builder.Build().Run();