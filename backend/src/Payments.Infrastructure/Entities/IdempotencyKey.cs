namespace Payments.Infrastructure.Entities;

/// <summary>
/// Stored idempotency record. Same (merchant, operation, key) with the same
/// request hash replays the stored response; a different hash is a conflict.
/// </summary>
public sealed class IdempotencyKey
{
    public required string Key { get; init; }
    public required string MerchantId { get; init; }

    /// <summary>"create" | "capture" | "refund".</summary>
    public required string Operation { get; init; }

    /// <summary>SHA-256 of the canonicalized request payload.</summary>
    public required string RequestHash { get; init; }

    /// <summary>HTTP status of the original response, replayed on retry.</summary>
    public required int ResponseStatusCode { get; set; }

    /// <summary>Serialized original response body, replayed on retry.</summary>
    public required string ResponseBody { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}
