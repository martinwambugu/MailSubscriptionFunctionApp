using MailSubscriptionFunctionApp.Infrastructure;
using MailSubscriptionFunctionApp.Interfaces;
using MailSubscriptionFunctionApp.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
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
            _graphClient = graphClient;
            _repository = repository;
        }

        /// <summary>  
        /// Creates a Microsoft Graph subscription for a user's mailbox and saves it in PostgreSQL.  
        /// </summary>  
        /// <param name="req">HTTP request containing userId in JSON body.</param>  
        /// <returns>HTTP response with subscription details or error message.</returns>  
        [Function("CreateMailSubscription")]
        [OpenApiOperation(operationId: "CreateMailSubscription",tags: new[] { "Subscriptions" },Summary = "Create Microsoft Graph mail subscription for a user",
            Description = "Creates a Microsoft Graph subscription for a user's mailbox and saves it in PostgreSQL.")]
        [OpenApiSecurity("ApiKeyAuth",SecuritySchemeType.ApiKey,Name = "x-api-key",In = OpenApiSecurityLocationType.Header)]
        [OpenApiRequestBody("application/json",typeof(object),Description = "Request body containing 'userId' property.")]
        [OpenApiResponseWithBody(HttpStatusCode.OK,"application/json",typeof(object),Description = "Subscription created successfully.")]
        [OpenApiResponseWithBody(HttpStatusCode.BadRequest,"application/json",typeof(object),Description = "Invalid input.")]
        [OpenApiResponseWithBody(HttpStatusCode.InternalServerError,"application/json",typeof(object),Description = "Internal error.")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = "subscriptions")] HttpRequestData req)
        {
            LogStart(nameof(CreateMailSubscription));

            try
            {
                var body = await JsonDocument.ParseAsync(req.Body);

                if (!body.RootElement.TryGetProperty("userId", out var userIdElement) ||
                    string.IsNullOrWhiteSpace(userIdElement.GetString()))
                {
                    _logger.LogWarning("Invalid or missing userId in request body.");
                    return await BadRequest(req, "Invalid or missing userId.");
                }

                var userId = userIdElement.GetString()!;
                _logger.LogInformation("Creating subscription for UserId: {UserId}", userId);

                var subscription = await _graphClient.CreateMailSubscriptionAsync(userId);
                await _repository.SaveSubscriptionAsync(subscription);

                _telemetry.TrackEvent("MailSubscription_Created", new Dictionary<string, string>
                {
                    { "UserId", userId },
                    { "SubscriptionId", subscription.SubscriptionId }
                });

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    message = "Subscription created successfully.",
                    subscriptionId = subscription.SubscriptionId
                });

                LogEnd(nameof(CreateMailSubscription), new { subscription.SubscriptionId });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating subscription.");
                _telemetry.TrackException(ex);
                return await InternalServerError(req, "An error occurred while creating the subscription.");
            }
        }
    }
}