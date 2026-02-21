using MailSubscriptionFunctionApp.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Interfaces
{
    /// <summary>  
    /// Defines persistence operations for <see cref="MailSubscription"/> entities.  
    /// </summary>  
    public interface IMailSubscriptionRepository
    {
        /// <summary>  
        /// Saves a mail subscription record to the database.  
        /// </summary>  
        Task SaveSubscriptionAsync(MailSubscription subscription, CancellationToken cancellationToken = default);

        /// <summary>  
        /// Resolves an email address to the internal user UUID.  
        /// </summary>  
        Task<string?> GetUserIdByEmailAsync(string email, CancellationToken cancellationToken = default);

        /// <summary>  
        /// Returns all subscriptions, optionally filtered by internal user UUID.  
        /// </summary>  
        Task<IEnumerable<MailSubscription>> GetAllSubscriptionsAsync(string? userId = null, CancellationToken cancellationToken = default);

        /// <summary>  
        /// Returns a single subscription by its Graph subscription ID, or null if not found.  
        /// </summary>  
        Task<MailSubscription?> GetSubscriptionByIdAsync(string subscriptionId, CancellationToken cancellationToken = default);

        /// <summary>  
        /// Deletes a subscription record from the database by its Graph subscription ID.  
        /// Returns true if a row was deleted, false if no row matched.  
        /// </summary>  
        Task<bool> DeleteSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default);
    }
}