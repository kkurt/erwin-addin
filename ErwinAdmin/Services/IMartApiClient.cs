using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EliteSoft.Erwin.Admin.Models;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// Interface for erwin Mart Server API client
    /// </summary>
    public interface IMartApiClient : IDisposable
    {
        /// <summary>
        /// Gets whether the client is currently authenticated
        /// </summary>
        bool IsAuthenticated { get; }

        /// <summary>
        /// Gets the current bearer token
        /// </summary>
        string BearerToken { get; }

        /// <summary>
        /// Event raised when authentication state changes
        /// </summary>
        event EventHandler<AuthenticationStateChangedEventArgs> AuthenticationStateChanged;

        /// <summary>
        /// Event raised for logging/debugging purposes
        /// </summary>
        event EventHandler<LogEventArgs> LogMessage;

        /// <summary>
        /// Authenticates with the Mart Server
        /// </summary>
        Task<AuthenticationResult> AuthenticateAsync(
            MartConnectionInfo connectionInfo,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets catalog children from the Mart Server
        /// </summary>
        Task<CatalogResult> GetCatalogChildrenAsync(
            string parentId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets user information
        /// </summary>
        Task<UserInfo> GetUserInfoAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears the current session
        /// </summary>
        void ClearSession();
    }

    public class AuthenticationStateChangedEventArgs : EventArgs
    {
        public bool IsAuthenticated { get; }
        public string Username { get; }

        public AuthenticationStateChangedEventArgs(bool isAuthenticated, string username = null)
        {
            IsAuthenticated = isAuthenticated;
            Username = username;
        }
    }

    public class LogEventArgs : EventArgs
    {
        public LogLevel Level { get; }
        public string Message { get; }
        public Exception Exception { get; }

        public LogEventArgs(LogLevel level, string message, Exception exception = null)
        {
            Level = level;
            Message = message;
            Exception = exception;
        }
    }

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }
}
