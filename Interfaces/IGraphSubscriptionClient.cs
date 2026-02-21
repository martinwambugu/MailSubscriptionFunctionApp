using MailSubscriptionFunctionApp.Models;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Interfaces
{
    /// <summary>  
    /// Abstraction for managing Microsoft Graph mail subscriptions.  
    /// </summary>  
    public interface IGraphSubscriptionClient
    {
        /// <summary>  
        /// Creates a new Microsoft Graph mail subscription for a user.  
        /// </summary>  
        Task<MailSubscription> CreateMailSubscriptionAsync(string userId, CancellationToken cancellationToken = default);

        /// <summary>  
        /// Deletes a Microsoft Graph subscription by its subscription ID.  
        /// Returns true on success, false if the subscription was not found in Graph.  
        /// </summary>  
        Task<bool> DeleteMailSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default);
    }
}