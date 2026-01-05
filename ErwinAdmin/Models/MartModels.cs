using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EliteSoft.Erwin.Admin.Models
{
    /// <summary>
    /// Connection information for Mart Server
    /// </summary>
    public class MartConnectionInfo
    {
        public string ServerName { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public string BaseUrl => $"http://{ServerName}:{Port}";

        public string ConnectionString =>
            $"SRV={ServerName};PRT={Port};UID={Username};PSW={Password}";

        public string GetMartUrl(string modelPath) =>
            $"mart://Mart/{modelPath}?{ConnectionString}";
    }

    /// <summary>
    /// Result of authentication attempt
    /// </summary>
    public class AuthenticationResult
    {
        public bool Success { get; set; }
        public string Token { get; set; }
        public string ErrorMessage { get; set; }
        public UserInfo UserInfo { get; set; }

        public static AuthenticationResult Successful(string token, UserInfo userInfo = null) =>
            new AuthenticationResult { Success = true, Token = token, UserInfo = userInfo };

        public static AuthenticationResult Failed(string errorMessage) =>
            new AuthenticationResult { Success = false, ErrorMessage = errorMessage };
    }

    /// <summary>
    /// User information from Mart Server
    /// </summary>
    public class UserInfo
    {
        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; }

        [JsonPropertyName("userType")]
        public string UserType { get; set; }

        [JsonPropertyName("isInternal")]
        public string IsInternal { get; set; }

        [JsonPropertyName("appType")]
        public string AppType { get; set; }

        [JsonPropertyName("profilePic")]
        public string ProfilePic { get; set; }
    }

    /// <summary>
    /// Wrapper for API responses with data property
    /// </summary>
    public class ApiResponse<T>
    {
        [JsonPropertyName("data")]
        public T Data { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }
    }

    /// <summary>
    /// Authentication response containing tokens
    /// </summary>
    public class AuthTokenResponse
    {
        [JsonPropertyName("id_token")]
        public string IdToken { get; set; }

        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; }

        [JsonPropertyName("reset_flag")]
        public string ResetFlag { get; set; }

        [JsonPropertyName("new_device")]
        public string NewDevice { get; set; }
    }

    /// <summary>
    /// Result of catalog query
    /// </summary>
    public class CatalogResult
    {
        public bool Success { get; set; }
        public List<CatalogEntry> Entries { get; set; } = new List<CatalogEntry>();
        public string ErrorMessage { get; set; }

        public static CatalogResult Successful(List<CatalogEntry> entries) =>
            new CatalogResult { Success = true, Entries = entries };

        public static CatalogResult Failed(string errorMessage) =>
            new CatalogResult { Success = false, ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Catalog entry from Mart Server
    /// </summary>
    public class CatalogEntry
    {
        [JsonPropertyName("entryid")]
        public string EntryId { get; set; }

        [JsonPropertyName("entryparent")]
        public string EntryParent { get; set; }

        [JsonPropertyName("entryname")]
        public string EntryName { get; set; }

        [JsonPropertyName("entrytype")]
        public string EntryType { get; set; }

        [JsonPropertyName("entrydescription")]
        public string EntryDescription { get; set; }

        [JsonPropertyName("entryishidden")]
        public string EntryIsHidden { get; set; }

        [JsonPropertyName("entrycreatedon")]
        public string EntryCreatedOn { get; set; }

        [JsonPropertyName("entrycreatedby")]
        public string EntryCreatedBy { get; set; }

        [JsonPropertyName("entryversion")]
        public string EntryVersion { get; set; }

        [JsonPropertyName("entryisversioning")]
        public string EntryIsVersioning { get; set; }

        [JsonPropertyName("entryhaschildren")]
        public string EntryHasChildren { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonIgnore]
        public bool HasChildren => EntryHasChildren?.Equals("Y", StringComparison.OrdinalIgnoreCase) == true;

        [JsonIgnore]
        public CatalogEntryType Type
        {
            get
            {
                switch (EntryType)
                {
                    case "M": return CatalogEntryType.Mart;
                    case "L": return CatalogEntryType.Library;
                    case "C": return CatalogEntryType.Category;
                    case "D": return CatalogEntryType.ModelGroup;  // Model directory/container
                    case "V": return CatalogEntryType.Model;       // Version = actual loadable model
                    case "O": return CatalogEntryType.Model;       // Object (legacy)
                    default: return CatalogEntryType.Unknown;
                }
            }
        }
    }

    /// <summary>
    /// Type of catalog entry
    /// </summary>
    public enum CatalogEntryType
    {
        Unknown,
        Mart,
        Library,
        Category,
        ModelGroup,  // "D" - Model directory containing versions
        Model        // "V" or "O" - Actual loadable model version
    }

    /// <summary>
    /// Response wrapper for catalog children
    /// </summary>
    public class CatalogChildrenResponse
    {
        [JsonPropertyName("getChildCatalogs")]
        public List<CatalogEntry> GetChildCatalogs { get; set; } = new List<CatalogEntry>();
    }

    /// <summary>
    /// App data from Mart Server
    /// </summary>
    public class MartAppData
    {
        [JsonPropertyName("authKey")]
        public string AuthKey { get; set; }

        [JsonPropertyName("authType")]
        public string AuthType { get; set; }
    }
}
