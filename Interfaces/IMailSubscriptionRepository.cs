using MailSubscriptionFunctionApp.Models;
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
        /// <param name="subscription">Subscription object to persist.</param>  
        Task SaveSubscriptionAsync(MailSubscription subscription);
    }
}