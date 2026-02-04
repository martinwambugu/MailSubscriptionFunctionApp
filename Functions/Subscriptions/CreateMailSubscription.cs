using MailSubscriptionFunctionApp.Infrastructure;
using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Resolvers;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Functions.Subscriptions
{
    /// <summary>  
    /// Azure Function endpoint for creating and persisting Microsoft Graph mail subscriptions.  
    /// </summary>  
    public class CreateMailSubscription : BaseFunction
    {
        private readonly IGraphSubscriptionClient _graphClient;
        private readonly IMailSubscriptionRepository _repository;

        public CreateMailSubscription(
            IGraphSubscriptionClient graphClient,
            IMailSubscriptionRepository repository,
            ILogger<CreateMailSubscription> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>  
        /// Creates a Microsoft Graph subscription for a user's mailbox and saves it in PostgreSQL.  
        /// </summary>  
        /// <param name="req">HTTP request containing userId in JSON body.</param>  
        /// <returns>HTTP response with subscription details or error message.</returns>  
        [Function("CreateMailSubscription")]
        [OpenApiOperation(
            operationId: "CreateMailSubscription",
            tags: new[] { "Subscriptions" },
            Summary = "Create Microsoft Graph mail subscription for a user",
            Description = "Creates a Microsoft Graph subscription for a user's mailbox and saves it in PostgreSQL.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiRequestBody(
            "application/json",
            typeof(CreateSubscriptionRequest),
            Description = "Request body containing 'userId' property.",
            Example = typeof(CreateSubscriptionRequestExample))]
        [OpenApiResponseWithBody(
            HttpStatusCode.Created,
            "application/json",
            typeof(CreateSubscriptionResponse),
            Description = "Subscription created successfully.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.BadRequest,
            "application/json",
            typeof(ErrorResponse),
            Description = "Invalid input (missing or empty userId).")]
        [OpenApiResponseWithBody(
            HttpStatusCode.InternalServerError,
            "application/json",
            typeof(ErrorResponse),
            Description = "Internal error occurred.")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "subscriptions")]
            HttpRequestData req,
            CancellationToken cancellationToken = default)
        {
            LogStart(nameof(CreateMailSubscription));

            try
            {
                // ✅ Step 1: Validate Content-Type  
                if (!req.Headers.TryGetValues("Content-Type", out var contentTypes) ||
                    !string.Join(",", contentTypes).Contains("application/json", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("⚠️ Invalid Content-Type. Expected 'application/json'.");
                    return await BadRequest(req, "Content-Type must be 'application/json'.");
                }

                // ✅ Step 2: Read request body as string first  
                string requestBodyText;
                try
                {
                    using var reader = new StreamReader(req.Body);
                    requestBodyText = await reader.ReadToEndAsync(cancellationToken);
                }
                catch (Exception readEx)
                {
                    _logger.LogError(readEx, "❌ Failed to read request body stream.");
                    return await InternalServerError(req, "Failed to read request body.");
                }

                // ✅ Step 3: Validate body is not empty  
                if (string.IsNullOrWhiteSpace(requestBodyText))
                {
                    _logger.LogWarning("⚠️ Empty request body received.");
                    return await BadRequest(req, "Request body cannot be empty. Expected JSON with 'userId' field.");
                }

                _logger.LogDebug("📥 Request body: {Body}", requestBodyText);

                // ✅ Step 4: Parse JSON with proper error handling  
                CreateSubscriptionRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<CreateSubscriptionRequest>(
                        requestBodyText,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true,
                            ReadCommentHandling = JsonCommentHandling.Skip
                        });
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "❌ Invalid JSON in request body: {Body}", requestBodyText);
                    _telemetry.TrackException(jsonEx, new Dictionary<string, string>
                    {
                        { "Operation", "CreateMailSubscription" },
                        { "ErrorType", "JsonParseError" },
                        { "RequestBody", requestBodyText.Length > 500 ? requestBodyText.Substring(0, 500) : requestBodyText }
                    });

                    return await BadRequest(req, $"Invalid JSON format: {jsonEx.Message}");
                }

                // ✅ Step 5: Validate userId field  
                if (request == null || string.IsNullOrWhiteSpace(request.UserId))
                {
                    _logger.LogWarning("⚠️ Missing or empty 'userId' in request.");
                    return await BadRequest(req, "The 'userId' field is required and cannot be empty.");
                }

                _logger.LogInformation("📧 Creating subscription for UserId: {UserId}", request.UserId);

                // ✅ Step 6: Create Graph subscription  
                var subscription = await _graphClient.CreateMailSubscriptionAsync(
                    request.UserId,
                    cancellationToken);

                // ✅ Step 7: Persist to database  
                await _repository.SaveSubscriptionAsync(subscription, cancellationToken);

                _telemetry.TrackEvent("MailSubscription_Created", new Dictionary<string, string>
                {
                    { "UserId", request.UserId },
                    { "SubscriptionId", subscription.SubscriptionId }
                });

                _logger.LogInformation("✅ Subscription created successfully. SubscriptionId: {SubscriptionId}",
                    subscription.SubscriptionId);

                // ✅ Step 8: Return success response  
                var response = req.CreateResponse(HttpStatusCode.Created);
                await response.WriteAsJsonAsync(new CreateSubscriptionResponse
                {
                    Message = "Subscription created successfully.",
                    SubscriptionId = subscription.SubscriptionId,
                    UserId = subscription.UserId,
                    ExpirationDateTime = subscription.SubscriptionExpirationTime,
                    NotificationUrl = subscription.NotificationUrl
                }, cancellationToken);

                LogEnd(nameof(CreateMailSubscription), new { subscription.SubscriptionId });
                return response;
            }
            catch (InvalidOperationException opEx)
            {
                // ✅ Handle known business logic errors (e.g., Graph API failures)  
                _logger.LogError(opEx, "❌ Operation failed: {Message}", opEx.Message);
                _telemetry.TrackException(opEx, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscription" },
                    { "ErrorType", "InvalidOperation" }
                });
                return await InternalServerError(req, opEx.Message);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ Request was cancelled.");
                var response = req.CreateResponse(HttpStatusCode.RequestTimeout);
                await response.WriteStringAsync("Request was cancelled or timed out.", cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error creating subscription.");
                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "CreateMailSubscription" }
                });
                return await InternalServerError(req, "An unexpected error occurred while creating the subscription.");
            }
        }
    }

    #region Request/Response Models  

    /// <summary>  
    /// Request model for creating a mail subscription.  
    /// </summary>  
    public class CreateSubscriptionRequest
    {
        /// <summary>  
        /// The user ID (email address) for which to create the subscription.  
        /// </summary>  
        /// <example>user@crdbbank.co.tz</example>  
        public string UserId { get; set; } = string.Empty;
    }

    /// <summary>  
    /// Response model for successful subscription creation.  
    /// </summary>  
    public class CreateSubscriptionResponse
    {
        /// <summary>Success message.</summary>  
        public string Message { get; set; } = string.Empty;

        /// <summary>The unique subscription ID from Microsoft Graph.</summary>  
        public string SubscriptionId { get; set; } = string.Empty;

        /// <summary>The user ID for whom the subscription was created.</summary>  
        public string UserId { get; set; } = string.Empty;

        /// <summary>When the subscription expires (UTC).</summary>  
        public DateTime ExpirationDateTime { get; set; }

        /// <summary>The notification endpoint URL.</summary>  
        public string NotificationUrl { get; set; } = string.Empty;
    }

    /// <summary>  
    /// Error response model.  
    /// </summary>  
    public class ErrorResponse
    {
        /// <summary>Error message.</summary>  
        public string Error { get; set; } = string.Empty;
    }

    /// <summary>  
    /// OpenAPI example for request body.  
    /// </summary>  
    public class CreateSubscriptionRequestExample : OpenApiExample<CreateSubscriptionRequest>
    {
        public override IOpenApiExample<CreateSubscriptionRequest> Build(NamingStrategy namingStrategy = null!)
        {
            Examples.Add(OpenApiExampleResolver.Resolve(
                "example1",
                new CreateSubscriptionRequest { UserId = "user@crdbbank.co.tz" },
                namingStrategy));
            return this;
        }
    }

    #endregion
}