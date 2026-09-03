using System.Security.Cryptography;
using System.Text;

namespace DBToRestAPI.Services;

/// <summary>
/// A short, stable, one-way identity for a value that must not appear in logs or in memory
/// longer than necessary: an API key, an e-mail address. Sixteen hex characters of SHA-256 —
/// enough to tell callers apart, useless for recovering the value.
/// </summary>
public static class IdentityHash
{
    public static string Short(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
