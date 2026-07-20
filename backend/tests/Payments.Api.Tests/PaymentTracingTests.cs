using System.Collections.Concurrent;
using System.Diagnostics;

namespace Payments.Api.Tests;

/// <summary>
/// The shared fixture host never registers OpenTelemetry (OTEL_EXPORTER_OTLP_ENDPOINT is unset
/// in tests), so Activity.Current is null there and tagging code would run without erroring
/// either way. Subscribing our own ActivityListener is what makes .NET's Activity
/// infrastructure start populating activities for the built-in ASP.NET Core hosting source,
/// independent of the app's own OTel DI registration - the same mechanism SettlementWorker's
/// own doc comment describes ("a null listener makes StartActivity a cheap no-op"). That lets
/// these tests prove the tag actually lands on an activity a real listener (Tempo, in
/// production) would see.
/// </summary>
[Collection(nameof(PaymentsApiCollection))]
public class PaymentTracingTests(PaymentsApiFixture fixture)
{
    private PaymentsApiClient NewMerchant() =>
        PaymentsApiClient.For(fixture.Api.CreateClient(), $"m-{Guid.NewGuid():N}");

    /// <summary>
    /// Filtering by the freshly-minted payment id keeps this safe under xUnit's default
    /// parallel test execution: no other activity in the whole process will happen to carry
    /// the same guid, so concurrent unrelated tests can't produce a false positive or negative.
    /// Every request touching a given payment tags it with the same payment.id - that's the
    /// whole point, so a payment with several requests against it legitimately has several
    /// tagged activities, not one.
    ///
    /// TagObjects, not the legacy Tags: Tags silently drops any tag whose value isn't already
    /// a string, and SetTag("payment.id", someGuid) stores a Guid - TagObjects is the complete
    /// list, and it's what a real OTLP exporter actually reads.
    /// </summary>
    private static List<Activity> TaggedActivities(IEnumerable<Activity> activities, Guid paymentId) =>
        activities
            .Where(a => a.TagObjects.Any(t => t.Key == "payment.id" && t.Value?.ToString() == paymentId.ToString()))
            .ToList();

    private static IDisposable ListenForActivities(out ConcurrentBag<Activity> activities)
    {
        var collected = new ConcurrentBag<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = collected.Add,
        };
        ActivitySource.AddActivityListener(listener);
        activities = collected;
        return listener;
    }

    [Fact]
    public async Task Create_tags_the_request_activity_with_the_new_payment_id()
    {
        using var listener = ListenForActivities(out var activities);
        var api = NewMerchant();

        var payment = await api.CreateAuthorized();

        var tagged = TaggedActivities(activities, payment.Id);
        Assert.Single(tagged);
        Assert.Contains(tagged[0].TagObjects, t => t.Key == "correlation.id");
    }

    [Fact]
    public async Task Capture_tags_the_request_activity_with_the_payment_id()
    {
        using var listener = ListenForActivities(out var activities);
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();

        await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());

        // Create's own tagged activity plus capture's own - both requests touched this payment.
        var tagged = TaggedActivities(activities, payment.Id);
        Assert.Equal(2, tagged.Count);
        Assert.All(tagged, a => Assert.Contains(a.TagObjects, t => t.Key == "correlation.id"));
    }

    [Fact]
    public async Task Refund_tags_the_request_activity_with_the_payment_id()
    {
        using var listener = ListenForActivities(out var activities);
        var api = NewMerchant();
        var payment = await api.CreateAuthorized();
        await api.CaptureRaw(payment.Id, Guid.NewGuid().ToString());

        await api.RefundRaw(payment.Id, "customer request", Guid.NewGuid().ToString());

        var tagged = TaggedActivities(activities, payment.Id);
        Assert.Equal(3, tagged.Count);
        Assert.All(tagged, a => Assert.Contains(a.TagObjects, t => t.Key == "correlation.id"));
    }
}
