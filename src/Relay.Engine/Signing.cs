using System.Security.Cryptography;
using System.Text;

namespace Relay.Engine;

public enum VerifyResult
{
    Valid,
    Missing,
    Malformed,
    Mismatch,
    OutsideTolerance,
}

public static class Headers
{
    public const string Stripe = "Stripe-Signature";
    public const string GitHub = "X-Hub-Signature-256";
    public const string EventId = "Relay-Event-Id";
    public const string DeliveryId = "Relay-Delivery-Id";
    public const string Attempt = "Relay-Attempt";
    public const string EventType = "Relay-Event-Type";
    public const string Signature = "Relay-Signature";
}

/// <summary>Checks what providers send us, and signs what we send to destinations.</summary>
public static class Signing
{
    // ---- Inbound: verifying a provider ----

    public static VerifyResult VerifyInbound(SignatureScheme scheme, IReadOnlyDictionary<string, string> headers, string body, IReadOnlyCollection<string> secrets, DateTimeOffset now, TimeSpan tolerance)
    {
        switch (scheme)
        {
            case SignatureScheme.None:
                return VerifyResult.Valid;
            case SignatureScheme.HmacBody:
                return VerifyGitHub(headers, body, secrets);
            default:
                return VerifyStripe(headers, body, secrets, now, tolerance);
        }
    }

    public static (string Name, string Value) SignInbound(SignatureScheme scheme, string secret, long timestamp, string body) => scheme switch
    {
        SignatureScheme.Timestamped => (Headers.Stripe, $"t={timestamp},v1={Hmac(secret, $"{timestamp}.{body}")}"),
        SignatureScheme.HmacBody => (Headers.GitHub, "sha256=" + Hmac(secret, body)),
        _ => ("", ""),
    };

    private static VerifyResult VerifyStripe(IReadOnlyDictionary<string, string> headers, string body, IReadOnlyCollection<string> secrets, DateTimeOffset now, TimeSpan tolerance)
    {
        if (!headers.TryGetValue(Headers.Stripe, out var header) || string.IsNullOrWhiteSpace(header))
        {
            return VerifyResult.Missing;
        }

        long? timestamp = null;
        var candidates = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return VerifyResult.Malformed;
            }

            var name = part[..eq];
            var value = part[(eq + 1)..];
            if (name == "t")
            {
                if (!long.TryParse(value, out var t))
                {
                    return VerifyResult.Malformed;
                }

                timestamp = t;
            }
            else if (name == "v1")
            {
                try
                {
                    candidates.Add(Convert.FromHexString(value));
                }
                catch (FormatException)
                {
                    return VerifyResult.Malformed;
                }
            }
        }

        if (timestamp is null || candidates.Count == 0)
        {
            return VerifyResult.Malformed;
        }

        if (!AnyMatch(secrets, $"{timestamp}.{body}", candidates))
        {
            return VerifyResult.Mismatch;
        }

        var age = Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).TotalSeconds);
        return age > tolerance.TotalSeconds ? VerifyResult.OutsideTolerance : VerifyResult.Valid;
    }

    private static VerifyResult VerifyGitHub(IReadOnlyDictionary<string, string> headers, string body, IReadOnlyCollection<string> secrets)
    {
        if (!headers.TryGetValue(Headers.GitHub, out var header) || string.IsNullOrWhiteSpace(header))
        {
            return VerifyResult.Missing;
        }

        const string prefix = "sha256=";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return VerifyResult.Malformed;
        }

        byte[] candidate;
        try
        {
            candidate = Convert.FromHexString(header[prefix.Length..]);
        }
        catch (FormatException)
        {
            return VerifyResult.Malformed;
        }

        return AnyMatch(secrets, body, [candidate]) ? VerifyResult.Valid : VerifyResult.Mismatch;
    }

    private static bool AnyMatch(IReadOnlyCollection<string> secrets, string signedText, List<byte[]> candidates)
    {
        var matched = false;
        foreach (var secret in secrets)
        {
            var expected = Convert.FromHexString(Hmac(secret, signedText));
            foreach (var candidate in candidates)
            {
                // Constant-time, so the comparison does not leak how many bytes were right.
                matched |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        return matched;
    }

    // ---- Outbound: what a destination uses to trust us ----

    /// <summary>The signed text is "{eventId}.{timestamp}.{body}", so the id and time are protected as well as the body.</summary>
    public static string SignOutbound(string secret, Guid eventId, long timestamp, string body) =>
        $"t={timestamp},v1={Hmac(secret, $"{eventId}.{timestamp}.{body}")}";

    public static VerifyResult VerifyOutbound(string? header, Guid eventId, string body, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return VerifyResult.Missing;
        }

        long? timestamp = null;
        string? v1 = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                return VerifyResult.Malformed;
            }

            if (part[..eq] == "t" && long.TryParse(part[(eq + 1)..], out var t))
            {
                timestamp = t;
            }
            else if (part[..eq] == "v1")
            {
                v1 = part[(eq + 1)..];
            }
        }

        if (timestamp is null || v1 is null)
        {
            return VerifyResult.Malformed;
        }

        byte[] candidate;
        try
        {
            candidate = Convert.FromHexString(v1);
        }
        catch (FormatException)
        {
            return VerifyResult.Malformed;
        }

        var expected = Convert.FromHexString(Hmac(secret, $"{eventId}.{timestamp}.{body}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, candidate))
        {
            return VerifyResult.Mismatch;
        }

        var age = Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).TotalSeconds);
        return age > tolerance.TotalSeconds ? VerifyResult.OutsideTolerance : VerifyResult.Valid;
    }

    public static string NewSecret(string prefix) => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static string Hmac(string secret, string text) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
