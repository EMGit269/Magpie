using System;
using System.Security.Cryptography;
using System.Text;

namespace Magpie
{
    /// <summary>
    /// 使用 Windows DPAPI（当前用户范围）保护 API Key；失败时可由调用方回退到明文存储。
    /// </summary>
    public static class ApiCredentialStore
    {
        public static bool TryProtectToBase64(string plainText, out string base64Cipher)
        {
            base64Cipher = null;
            if (plainText == null) return false;
            byte[] plain = Encoding.UTF8.GetBytes(plainText);
            try
            {
                byte[] blob = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
                base64Cipher = Convert.ToBase64String(blob);
                return true;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("ApiCredentialStore.Protect failed: " + ex.Message);
                return false;
            }
        }

        public static bool TryUnprotectFromBase64(string base64Cipher, out string plainText)
        {
            plainText = null;
            if (string.IsNullOrWhiteSpace(base64Cipher)) return false;
            try
            {
                byte[] blob = Convert.FromBase64String(base64Cipher.Trim());
                byte[] plain = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
                plainText = Encoding.UTF8.GetString(plain);
                return true;
            }
            catch (Exception ex)
            {
                AddGhLog.Warn("ApiCredentialStore.Unprotect failed: " + ex.Message);
                return false;
            }
        }
    }
}
