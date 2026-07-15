using Payments.Api.Contracts;
using Payments.Api.Idempotency;

namespace Payments.Api.Tests;

/// <summary>
/// The fingerprint is what stops a reused idempotency key from replaying the wrong
/// response, so its two properties are worth pinning: identical requests must agree,
/// and anything semantically different must not.
/// </summary>
public class RequestHashTests
{
    private static CreatePaymentRequest Request(long amountMinor = 1000, string? description = "order 42") =>
        new(amountMinor, "USD", "tok_visa", "4242", "visa", description);

    [Fact]
    public void Identical_requests_hash_identically()
    {
        Assert.Equal(
            RequestHash.Compute("create", "acme", Request()),
            RequestHash.Compute("create", "acme", Request()));
    }

    [Fact]
    public void Changing_any_field_changes_the_hash()
    {
        var baseline = RequestHash.Compute("create", "acme", Request());

        Assert.NotEqual(baseline, RequestHash.Compute("create", "acme", Request(amountMinor: 1001)));
        Assert.NotEqual(baseline, RequestHash.Compute("create", "acme", Request(description: "order 43")));
        Assert.NotEqual(baseline, RequestHash.Compute("create", "acme", Request(description: null)));
    }

    [Fact]
    public void Same_payload_under_a_different_operation_or_merchant_does_not_collide()
    {
        var baseline = RequestHash.Compute("create", "acme", Request());

        Assert.NotEqual(baseline, RequestHash.Compute("capture", "acme", Request()));
        Assert.NotEqual(baseline, RequestHash.Compute("create", "globex", Request()));
    }

    [Fact]
    public void Hash_is_sha256_hex_and_fits_the_stored_column()
    {
        var hash = RequestHash.Compute("create", "acme", Request());

        Assert.Equal(64, hash.Length);
        Assert.True(hash.All(Uri.IsHexDigit));
    }
}
