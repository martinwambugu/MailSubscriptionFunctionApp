using System;

namespace MailSubscriptionFunctionApp.Models
{
    /// <summary>  
    /// Represents a Microsoft Graph mail subscription record stored in PostgreSQL table <c>MailSubscriptions</c>.  
    /// </summary>  
    public class MailSubscription
    {
        /// <summary>Primary key of the subscription.</summary>  
        public string SubscriptionId { get; set; } = default!;

        /// <summary>Azure AD user ID associated with the subscription.</summary>  
        public string UserId { get; set; } = default!;

        /// <summary>Start time of the subscription (UTC).</summary>  
        public DateTime SubscriptionStartTime { get; set; }

        /// <summary>Expiration time of the subscription (UTC).</summary>  
        public DateTime SubscriptionExpirationTime { get; set; }

        /// <summary>Webhook notification URL.</summary>  
        public string NotificationUrl { get; set; } = default!;

        /// <summary>Client state string used for validation.</summary>  
        public string ClientState { get; set; } = default!;

        /// <summary>Record creation timestamp.</summary>  
        public DateTime CreatedDateTime { get; set; }

        /// <summary>Last renewal timestamp (nullable).</summary>  
        public DateTime? LastRenewedDateTime { get; set; }

        /// <summary>Change type for the subscription (e.g., “created”).</summary>  
        public string SubscriptionChangeType { get; set; } = default!;

        /// <summary>Azure AD application ID used to create the subscription.</summary>  
        public string? ApplicationId { get; set; }

    }
}