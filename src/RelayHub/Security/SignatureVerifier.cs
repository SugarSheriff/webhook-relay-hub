using System.Security.Cryptography;
using System.Text;

namespace RelayHub.Security;

/// <summary>
/// Checks HMAC-SHA256 signatures, the "wax seal" senders like GitHub and Stripe put on webhooks.
/// The sender hashes the raw body with a shared secret; if our hash matches theirs,
/// the message really came from them and wasn't changed on the way.
/// </summary>
public static class SignatureVerifier
{
    public static string Sign(byte[] body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return Convert.ToHexStringLower(hash);
    }

    public static bool IsValid(byte[] body, string secret, string? header, string prefix)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header))
            return false;

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var expected = Encoding.ASCII.GetBytes(Sign(body, secret));
        var actual = Encoding.ASCII.GetBytes(header[prefix.Length..].Trim().ToLowerInvariant());

        // Fixed-time comparison: a normal == can leak how many characters matched
        // through tiny timing differences, which attackers can measure.
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
