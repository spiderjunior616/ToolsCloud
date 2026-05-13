using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ToolsCloud.Services;

/// <summary>
/// Protects sensitive strings (API keys, tokens) using Windows DPAPI.
/// Data is encrypted per-user — only the same Windows account can decrypt.
/// Falls back gracefully to plaintext on non-Windows or if DPAPI fails.
/// </summary>
internal static class SecureStorage
{
    /// <summary>
    /// Saves a value encrypted with DPAPI. The file is not human-readable.
    /// </summary>
    internal static void Save(string filePath, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
            return;
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(filePath, encrypted);
        }
        catch
        {
            // DPAPI unavailable — fall back to plaintext
            File.WriteAllText(filePath, value);
        }
    }

    /// <summary>
    /// Loads a DPAPI-encrypted value. Auto-detects and migrates plaintext files
    /// left over from older versions.
    /// </summary>
    internal static string Load(string filePath)
    {
        if (!File.Exists(filePath))
            return "";

        try
        {
            var raw = File.ReadAllBytes(filePath);
            if (raw.Length == 0)
                return "";

            // Try DPAPI decryption first
            try
            {
                var decrypted = ProtectedData.Unprotect(raw, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted).Trim();
            }
            catch (CryptographicException)
            {
                // Not DPAPI-encrypted — treat as legacy plaintext and migrate
                var plaintext = Encoding.UTF8.GetString(raw).Trim();
                if (!string.IsNullOrEmpty(plaintext))
                {
                    // Re-save with DPAPI protection
                    Save(filePath, plaintext);
                }
                return plaintext;
            }
        }
        catch
        {
            return "";
        }
    }
}
