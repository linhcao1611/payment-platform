using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Payments.Api.Idempotency;

/// <summary>
/// Fingerprints the request behind an idempotency key, so reusing a key with a
/// different payload is rejected rather than silently replaying the wrong response.
///
/// Canonicalization comes from hashing the <em>parsed DTO re-serialized</em> with fixed
/// options rather than the raw request bytes: property order, whitespace and casing in
/// the client's JSON don't change the fingerprint, but any semantic change does. The
/// operation and merchant are folded in so one key can't collide across operations or
/// tenants.
/// </summary>
public static class RequestHash
{
    private static readonly JsonSerializerOptions CanonicalOptions = new(JsonSerializerDefaults.Web);

    public static string Compute<TPayload>(string operation, string merchantId, TPayload payload)
    {
        var canonical = $"{operation}\n{merchantId}\n{JsonSerializer.Serialize(payload, CanonicalOptions)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
