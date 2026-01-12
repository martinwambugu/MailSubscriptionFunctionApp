using MailSubscriptionFunctionApp.Models;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Interfaces
{
    /// <summary>  
    /// Abstraction for creating Microsoft Graph subscriptions.  
    /// </summary>  
    public interface IGraphSubscriptionClient
    {
        /// <summary>  
        /// Creates a new Microsoft Graph mail subscription for a user.  
        /// </summary>  
        /// <param name="userId">Azure AD user ID.</param>  
        /// <returns>Newly created <see cref="MailSubscription"/> instance.</returns>  
        Task<MailSubscription> CreateMailSubscriptionAsync(string userId, CancellationToken cancellationToken = default); 
    }
}