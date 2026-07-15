using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Api.Tests;

public sealed record PageDto(List<PaymentDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);

/// <summary>
/// The list endpoint is the frontend's whole view of the system, and its filters take raw
/// user input — which is exactly where a 500 hides.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class PaymentsListTests(PaymentsApiFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private PaymentsApiClient NewMerchant() =>
        PaymentsApiClient.For(fixture.Api.CreateClient(), $"m-{Guid.NewGuid():N}");

    private static async Task<PageDto> Page(PaymentsApiClient api, string query)
    {
        var response = await api.Http.GetAsync($"/api/payments{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PageDto>(Json))!;
    }

    [Theory]
    // A bare date is what <input type="date"> sends; model binding stamps it with the server's
    // local offset. The rest are ordinary clients stating their own timezone.
    [InlineData("2020-01-01")]
    [InlineData("2020-01-01T00:00:00Z")]
    [InlineData("2020-01-01T00:00:00%2B02:00")]
    [InlineData("2020-01-01T00:00:00-07:00")]
    public async Task Date_filters_accept_any_offset_a_client_might_send(string from)
    {
        var api = NewMerchant();
        await api.CreateAuthorized();

        // Regression: Npgsql rejects a non-zero offset on `timestamp with time zone`, so before
        // normalizing to UTC at the boundary every one of these but the Z form was a 500.
        var page = await Page(api, $"?from={from}");

        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Date_filters_select_by_instant_not_by_wall_clock()
    {
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();

        // The same instant written three ways must filter identically — otherwise a merchant's
        // timezone silently changes which payments they can see.
        var justBefore = payment.CreatedAt.AddMinutes(-1);
        var asUtc = justBefore.ToUniversalTime().ToString("o");
        var asPlusTwo = justBefore.ToOffset(TimeSpan.FromHours(2)).ToString("o");
        var asMinusSeven = justBefore.ToOffset(TimeSpan.FromHours(-7)).ToString("o");

        foreach (var from in new[] { asUtc, asPlusTwo, asMinusSeven })
            Assert.Equal(1, (await Page(api, $"?from={Uri.EscapeDataString(from)}")).TotalCount);

        // And an instant after it must exclude it, whatever offset expresses that instant.
        var justAfter = payment.CreatedAt.AddMinutes(1);
        foreach (var offset in new[] { 0, 2, -7 })
            Assert.Equal(
                0,
                (await Page(api, $"?from={Uri.EscapeDataString(justAfter.ToOffset(TimeSpan.FromHours(offset)).ToString("o"))}"))
                    .TotalCount);
    }

    [Fact]
    public async Task An_unparseable_date_is_a_400_not_a_500()
    {
        var api = NewMerchant();

        var response = await api.Http.GetAsync("/api/payments?from=banana");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_status_filter_is_rejected_with_a_stable_code()
    {
        var api = NewMerchant();

        var response = await api.Http.GetAsync("/api/payments?status=Bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_status", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task A_backwards_date_range_is_rejected()
    {
        var api = NewMerchant();

        var response = await api.Http.GetAsync("/api/payments?from=2027-01-01T00:00:00Z&to=2020-01-01T00:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_date_range", (await PaymentsApiClient.Problem(response)).ErrorCode);
    }

    [Fact]
    public async Task Paging_never_repeats_or_drops_a_payment()
    {
        var api = NewMerchant();

        // Created in a tight loop so several share a created_at tick — which is precisely when
        // an unstable sort starts showing the same row twice and hiding another.
        for (var i = 0; i < 7; i++)
            await api.CreateAuthorized(amountMinor: 100 + i);

        var seen = new List<Guid>();
        for (var page = 1; page <= 4; page++)
            seen.AddRange((await Page(api, $"?pageSize=2&page={page}")).Items.Select(p => p.Id));

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task Status_filter_and_search_narrow_to_the_right_payments()
    {
        var api = NewMerchant();
        var target = await api.CreateAuthorized(amountMinor: 4200);
        var other = await api.CreateAuthorized(amountMinor: 999);
        await api.CaptureRaw(other.Id, Guid.NewGuid().ToString());

        var authorized = await Page(api, "?status=Authorized");
        Assert.Equal(target.Id, Assert.Single(authorized.Items).Id);

        // An exact id and a prefix of it must both find it.
        Assert.Equal(1, (await Page(api, $"?search={target.Id}")).TotalCount);
        Assert.Equal(1, (await Page(api, $"?search={target.Id.ToString()[..8]}")).TotalCount);
    }

    [Fact]
    public async Task Page_size_is_clamped_rather_than_rejected()
    {
        var api = NewMerchant();
        await api.CreateAuthorized();

        var page = await Page(api, "?pageSize=10000");

        // An over-eager client gets a bounded page, not a 400 — and can't ask for the table.
        Assert.Equal(100, page.PageSize);
    }
}
