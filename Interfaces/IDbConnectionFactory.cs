using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace MailSubscriptionFunctionApp.Interfaces
{
    /// <summary>  
    /// Defines a factory contract for creating database connections.  
    /// This abstraction allows the application to request a new database connection  
    /// without being tightly coupled to a specific database provider (e.g., PostgreSQL, SQL Server).  
    /// </summary>  
    /// <remarks>  
    /// <para>  
    /// Implementations of this interface should:  
    /// <list type="bullet">  
    ///     <item>Retrieve the connection string from a secure configuration source (e.g., Azure Key Vault, app settings).</item>  
    ///     <item>Instantiate and return an <see cref="IDbConnection"/> object specific to the target database provider.</item>  
    ///     <item>Open the connection asynchronously before returning it.</item>  
    ///     <item>Support cancellation via <see cref="CancellationToken"/> for long-running connection attempts.</item>  
    /// </list>  
    /// </para>  
    /// <para>  
    /// This interface supports Dependency Injection (DI) to decouple database connection logic  
    /// from repository/service classes, making the application easier to test and maintain.  
    /// </para>  
    /// </remarks>  
    /// <example>  
    /// Usage example:  
    /// <code>  
    /// public class MyRepository  
    /// {  
    ///     private readonly IDbConnectionFactory _dbFactory;  
    ///       
    ///     public MyRepository(IDbConnectionFactory dbFactory)  
    ///     {  
    ///         _dbFactory = dbFactory;  
    ///     }  
    ///       
    ///     public async Task&lt;User&gt; GetUserAsync(string userId, CancellationToken cancellationToken)  
    ///     {  
    ///         await using var connection = await _dbFactory.CreateConnectionAsync(cancellationToken);  
    ///         return await connection.QuerySingleAsync&lt;User&gt;("SELECT * FROM users WHERE id = @Id", new { Id = userId });  
    ///     }  
    /// }  
    /// </code>  
    /// </example>  
    public interface IDbConnectionFactory
    {
        /// <summary>  
        /// Creates and opens a new database connection asynchronously.  
        /// </summary>  
        /// <param name="cancellationToken">  
        /// A cancellation token that can be used to cancel the connection opening operation.  
        /// Passing <see cref="CancellationToken.None"/> will create a connection without cancellation support.  
        /// </param>  
        /// <returns>  
        /// A <see cref="Task{IDbConnection}"/> representing the asynchronous operation.  
        /// The task result contains an opened <see cref="IDbConnection"/> instance.  
        /// </returns>  
        /// <exception cref="InvalidOperationException">  
        /// Thrown when the connection string is not configured or the connection cannot be established.  
        /// </exception>  
        /// <exception cref="OperationCanceledException">  
        /// Thrown when the operation is cancelled via the <paramref name="cancellationToken"/>.  
        /// </exception>  
        /// <remarks>  
        /// <para>  
        /// The returned connection is already opened and ready to use.  
        /// Callers are responsible for disposing the connection when done (use <c>await using</c> pattern).  
        /// </para>  
        /// <para>  
        /// <b>Thread Safety:</b> This method is thread-safe and can be called concurrently from multiple threads.  
        /// </para>  
        /// <para>  
        /// <b>Performance:</b> Connections are pooled by default. Disposing a connection returns it to the pool  
        /// rather than closing the underlying database connection.  
        /// </para>  
        /// </remarks>  
        Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
    }
}