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
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Functions.Subscriptions
{
    /// <summary>
    /// Azure Function endpoints for retrieving mail subscription records.
    /// </summary>
    public class GetMailSubscriptions : BaseFunction
    {
        private readonly IMailSubscriptionRepository _repository;

        public GetMailSubscriptions(
            IMailSubscriptionRepository repository,
            ILogger<GetMailSubscriptions> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// Returns all mail subscriptions. Accepts an optional ?userId= query parameter
        /// to filter by the internal PostgreSQL user UUID.
        /// </summary>
        [Function("GetAllMailSubscriptions")]
        [OpenApiOperation(
            operationId: "GetAllMailSubscriptions",
            tags: new[] { "Subscriptions" },
            Summary = "List all mail subscriptions",
            Description = "Returns all subscription records from PostgreSQL, optionally filtered by userId.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(IEnumerable<MailSubscription>),
            Description = "List of subscriptions.")]
        public async Task<HttpResponseData> GetAll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "subscriptions")]
            HttpRequestData req,
            CancellationToken cancellationToken = default)
        {
            LogStart("GetAllMailSubscriptions");

            try
            {
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var userIdFilter = query["userId"];

                var subscriptions = await _repository.GetAllSubscriptionsAsync(userIdFilter, cancellationToken);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(subscriptions, cancellationToken);

                LogEnd("GetAllMailSubscriptions");
                return response;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ GetAllMailSubscriptions was cancelled.");
                var response = req.CreateResponse(HttpStatusCode.RequestTimeout);
                await response.WriteStringAsync("Request was cancelled.", cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error retrieving subscriptions.");
                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "GetAllMailSubscriptions" }
                });
                return await InternalServerError(req, "An unexpected error occurred while retrieving subscriptions.");
            }
        }

        /// <summary>
        /// Returns a single mail subscription by its Graph subscription ID.
        /// </summary>
        [Function("GetMailSubscriptionById")]
        [OpenApiOperation(
            operationId: "GetMailSubscriptionById",
            tags: new[] { "Subscriptions" },
            Summary = "Get a mail subscription by ID",
            Description = "Returns a single subscription record from PostgreSQL by its subscriptionId.")]
        [OpenApiSecurity(
            "ApiKeyAuth",
            SecuritySchemeType.ApiKey,
            Name = "x-api-key",
            In = OpenApiSecurityLocationType.Header)]
        [OpenApiParameter(
            name: "subscriptionId",
            In = ParameterLocation.Path,
            Required = true,
            Type = typeof(string),
            Description = "The Graph subscription ID.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(MailSubscription),
            Description = "Subscription found.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.NotFound,
            "application/json",
            typeof(ErrorResponse),
            Description = "Subscription not found.")]
        public async Task<HttpResponseData> GetById(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "subscriptions/{subscriptionId}")]
            HttpRequestData req,
            string subscriptionId,
            CancellationToken cancellationToken = default)
        {
            LogStart("GetMailSubscriptionById", new { subscriptionId });

            if (string.IsNullOrWhiteSpace(subscriptionId))
                return await BadRequest(req, "subscriptionId path parameter is required.");

            try
            {
                var subscription = await _repository.GetSubscriptionByIdAsync(subscriptionId, cancellationToken);

                if (subscription is null)
                    return await NotFound(req, $"Subscription '{subscriptionId}' was not found.");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(subscription, cancellationToken);

                LogEnd("GetMailSubscriptionById", new { subscriptionId });
                return response;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ GetMailSubscriptionById was cancelled for {SubscriptionId}.", subscriptionId);
                var response = req.CreateResponse(HttpStatusCode.RequestTimeout);
                await response.WriteStringAsync("Request was cancelled.", cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error retrieving subscription {SubscriptionId}.", subscriptionId);
                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "GetMailSubscriptionById" },
                    { "SubscriptionId", subscriptionId }
                });
                return await InternalServerError(req, "An unexpected error occurred while retrieving the subscription.");
            }
        }
    }
}
