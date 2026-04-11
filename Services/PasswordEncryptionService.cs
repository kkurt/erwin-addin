using System;
using System.Security.Cryptography;
using System.Text;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Static helper for encrypting/decrypting passwords using Windows DPAPI.
    /// Data is scoped to the current Windows user on the current machine.
    /// Must match erwin-admin's PasswordEncryptionService for compatibility.
    /// </summary>
    public static class PasswordEncryptionService
    {
        public static string Decrypt(string encryptedBase64)
        {
            return Decrypt(encryptedBase64, DataProtectionScope.CurrentUser);
        }

        public static string Decrypt(string encryptedBase64, DataProtectionScope scope)
        {
            if (string.IsNullOrEmpty(encryptedBase64))
                return null;

            try
            {
                var encrypted = Convert.FromBase64String(encryptedBase64);
                var decrypted = ProtectedData.Unprotect(encrypted, null, scope);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                return encryptedBase64;
            }
        }
    }
}
