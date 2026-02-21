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
    /// Azure Function endpoint to cancel and delete a mail subscription from both
    /// Microsoft Graph and the PostgreSQL database.
    /// </summary>
    public class DeleteMailSubscription : BaseFunction
    {
        private readonly IGraphSubscriptionClient _graphClient;
        private readonly IMailSubscriptionRepository _repository;

        public DeleteMailSubscription(
            IGraphSubscriptionClient graphClient,
            IMailSubscriptionRepository repository,
            ILogger<DeleteMailSubscription> logger,
            ICustomTelemetry telemetry)
            : base(logger, telemetry)
        {
            _graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// Deletes a Microsoft Graph mail subscription and removes the corresponding
        /// database record. If the subscription is not found in Graph (already expired/deleted),
        /// the database record is still removed.
        /// </summary>
        [Function("DeleteMailSubscription")]
        [OpenApiOperation(
            operationId: "DeleteMailSubscription",
            tags: new[] { "Subscriptions" },
            Summary = "Cancel and delete a mail subscription",
            Description = "Removes the subscription from Microsoft Graph and deletes the database record.")]
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
            Description = "The Graph subscription ID to delete.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.OK,
            "application/json",
            typeof(DeleteSubscriptionResponse),
            Description = "Subscription deleted successfully.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.NotFound,
            "application/json",
            typeof(ErrorResponse),
            Description = "Subscription not found in the database.")]
        [OpenApiResponseWithBody(
            HttpStatusCode.InternalServerError,
            "application/json",
            typeof(ErrorResponse),
            Description = "Internal error occurred.")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "subscriptions/{subscriptionId}")]
            HttpRequestData req,
            string subscriptionId,
            CancellationToken cancellationToken = default)
        {
            LogStart(nameof(DeleteMailSubscription), new { subscriptionId });

            if (string.IsNullOrWhiteSpace(subscriptionId))
                return await BadRequest(req, "subscriptionId path parameter is required.");

            try
            {
                // Confirm the record exists in our database before attempting Graph deletion
                var existing = await _repository.GetSubscriptionByIdAsync(subscriptionId, cancellationToken);

                if (existing is null)
                {
                    _logger.LogWarning("⚠️ Subscription {SubscriptionId} not found in database.", subscriptionId);
                    return await NotFound(req, $"Subscription '{subscriptionId}' was not found.");
                }

                // Attempt Graph deletion — 404 from Graph is treated as already removed
                var deletedFromGraph = await _graphClient.DeleteMailSubscriptionAsync(subscriptionId, cancellationToken);

                if (!deletedFromGraph)
                {
                    _logger.LogWarning(
                        "⚠️ Subscription {SubscriptionId} was not found in Graph (may have already expired). Removing DB record.",
                        subscriptionId);
                }

                // Always remove the database record
                await _repository.DeleteSubscriptionAsync(subscriptionId, cancellationToken);

                _telemetry.TrackEvent("MailSubscription_Deleted", new Dictionary<string, string>
                {
                    { "SubscriptionId", subscriptionId },
                    { "UserId", existing.UserId },
                    { "DeletedFromGraph", deletedFromGraph.ToString() }
                });

                _logger.LogInformation(
                    "✅ Subscription {SubscriptionId} deleted. RemovedFromGraph: {FromGraph}",
                    subscriptionId, deletedFromGraph);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new DeleteSubscriptionResponse
                {
                    Message = "Subscription deleted successfully.",
                    SubscriptionId = subscriptionId,
                    RemovedFromGraph = deletedFromGraph
                }, cancellationToken);

                LogEnd(nameof(DeleteMailSubscription), new { subscriptionId });
                return response;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⚠️ DeleteMailSubscription was cancelled for {SubscriptionId}.", subscriptionId);
                var response = req.CreateResponse(HttpStatusCode.RequestTimeout);
                await response.WriteStringAsync("Request was cancelled.", cancellationToken);
                return response;
            }
            catch (InvalidOperationException opEx)
            {
                _logger.LogError(opEx, "❌ Operation failed deleting subscription {SubscriptionId}.", subscriptionId);
                _telemetry.TrackException(opEx, new Dictionary<string, string>
                {
                    { "Operation", "DeleteMailSubscription" },
                    { "SubscriptionId", subscriptionId }
                });
                return await InternalServerError(req, opEx.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Unexpected error deleting subscription {SubscriptionId}.", subscriptionId);
                _telemetry.TrackException(ex, new Dictionary<string, string>
                {
                    { "Operation", "DeleteMailSubscription" },
                    { "SubscriptionId", subscriptionId }
                });
                return await InternalServerError(req, "An unexpected error occurred while deleting the subscription.");
            }
        }
    }

    /// <summary>Response model for a successful subscription deletion.</summary>
    public class DeleteSubscriptionResponse
    {
        public string Message { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        /// <summary>Indicates whether the subscription was found and removed from Microsoft Graph.</summary>
        public bool RemovedFromGraph { get; set; }
    }
}
