using Relay.Engine;

namespace Relay.Tests;

public class InboundSigningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    private const string Body = "{\"id\":\"evt_1\"}";

    private static VerifyResult Stripe(IReadOnlyDictionary<string, string> headers, string body = Body, params string[] secrets) =>
        Signing.VerifyInbound(SignatureScheme.Timestamped, headers, body, secrets.Length == 0 ? ["s3cret"] : secrets, Now, Tolerance);

    private static Dictionary<string, string> Signed(string secret = "s3cret", long? t = null, string body = Body)
    {
        var (name, value) = Signing.SignInbound(SignatureScheme.Timestamped, secret, t ?? Now.ToUnixTimeSeconds(), body);
        return new() { [name] = value };
    }

    [Fact]
    public void A_correctly_signed_stripe_style_request_is_valid() => Assert.Equal(VerifyResult.Valid, Stripe(Signed()));

    [Fact]
    public void A_missing_header_is_reported_as_missing() => Assert.Equal(VerifyResult.Missing, Stripe(new Dictionary<string, string>()));

    [Fact]
    public void A_blank_header_is_reported_as_missing() => Assert.Equal(VerifyResult.Missing, Stripe(new Dictionary<string, string> { [Headers.Stripe] = "  " }));

    [Theory]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("t=1760000000")]
    [InlineData("v1=abcd")]
    [InlineData("t=1760000000,v1=zz")]
    [InlineData("=x,t=1,v1=00")]
    public void A_malformed_header_is_reported_as_malformed(string header) =>
        Assert.Equal(VerifyResult.Malformed, Stripe(new Dictionary<string, string> { [Headers.Stripe] = header }));

    [Fact]
    public void The_wrong_secret_does_not_match() => Assert.Equal(VerifyResult.Mismatch, Stripe(Signed("other")));

    [Fact]
    public void A_changed_body_does_not_match() => Assert.Equal(VerifyResult.Mismatch, Stripe(Signed(), "{\"id\":\"evt_2\"}"));

    [Fact]
    public void A_changed_timestamp_does_not_match_because_the_time_is_signed_too()
    {
        var header = Signed(t: Now.ToUnixTimeSeconds() - 3600)[Headers.Stripe];
        var forged = header.Replace($"t={Now.ToUnixTimeSeconds() - 3600}", $"t={Now.ToUnixTimeSeconds()}");

        Assert.Equal(VerifyResult.Mismatch, Stripe(new Dictionary<string, string> { [Headers.Stripe] = forged }));
    }

    [Theory]
    [InlineData(-299, VerifyResult.Valid)]
    [InlineData(-300, VerifyResult.Valid)]
    [InlineData(-301, VerifyResult.OutsideTolerance)]
    [InlineData(300, VerifyResult.Valid)]
    [InlineData(301, VerifyResult.OutsideTolerance)]
    [InlineData(-86400, VerifyResult.OutsideTolerance)]
    public void The_timestamp_must_be_within_five_minutes_either_way(int offsetSeconds, VerifyResult expected) =>
        Assert.Equal(expected, Stripe(Signed(t: Now.ToUnixTimeSeconds() + offsetSeconds)));

    [Fact]
    public void An_old_timestamp_with_a_wrong_signature_is_a_mismatch_not_an_expiry() =>
        Assert.Equal(VerifyResult.Mismatch, Stripe(Signed("other", Now.ToUnixTimeSeconds() - 86400)));

    [Fact]
    public void Several_signatures_are_accepted_if_one_matches()
    {
        var good = Signed()[Headers.Stripe];
        var header = good + ",v1=" + new string('a', 64);

        Assert.Equal(VerifyResult.Valid, Stripe(new Dictionary<string, string> { [Headers.Stripe] = header }));
    }

    [Fact]
    public void Any_of_several_secrets_may_match_so_a_secret_can_be_rotated()
    {
        Assert.Equal(VerifyResult.Valid, Stripe(Signed("old"), Body, "new", "old"));
        Assert.Equal(VerifyResult.Valid, Stripe(Signed("new"), Body, "new", "old"));
        Assert.Equal(VerifyResult.Mismatch, Stripe(Signed("older"), Body, "new", "old"));
    }

    [Fact]
    public void Github_style_signs_the_body_alone()
    {
        var (name, value) = Signing.SignInbound(SignatureScheme.HmacBody, "s3cret", 0, Body);

        Assert.Equal(Headers.GitHub, name);
        Assert.StartsWith("sha256=", value);
        Assert.Equal(VerifyResult.Valid, Signing.VerifyInbound(SignatureScheme.HmacBody, new Dictionary<string, string> { [name] = value }, Body, ["s3cret"], Now, Tolerance));
    }

    [Fact]
    public void Github_style_ignores_the_clock_because_it_has_no_timestamp()
    {
        var (name, value) = Signing.SignInbound(SignatureScheme.HmacBody, "s3cret", 0, Body);
        var far = Now.AddYears(5);

        Assert.Equal(VerifyResult.Valid, Signing.VerifyInbound(SignatureScheme.HmacBody, new Dictionary<string, string> { [name] = value }, Body, ["s3cret"], far, Tolerance));
    }

    [Theory]
    [InlineData("", VerifyResult.Missing)]
    [InlineData("deadbeef", VerifyResult.Malformed)]
    [InlineData("sha256=nothex", VerifyResult.Malformed)]
    [InlineData("sha256=00", VerifyResult.Mismatch)]
    public void Github_style_rejects_bad_headers(string header, VerifyResult expected) =>
        Assert.Equal(expected, Signing.VerifyInbound(SignatureScheme.HmacBody, new Dictionary<string, string> { [Headers.GitHub] = header }, Body, ["s3cret"], Now, Tolerance));

    [Fact]
    public void Github_style_with_the_wrong_secret_or_body_does_not_match()
    {
        var (name, value) = Signing.SignInbound(SignatureScheme.HmacBody, "other", 0, Body);
        var headers = new Dictionary<string, string> { [name] = value };

        Assert.Equal(VerifyResult.Mismatch, Signing.VerifyInbound(SignatureScheme.HmacBody, headers, Body, ["s3cret"], Now, Tolerance));
        Assert.Equal(VerifyResult.Mismatch, Signing.VerifyInbound(SignatureScheme.HmacBody, headers, "{}", ["other"], Now, Tolerance));
    }

    [Fact]
    public void A_source_without_a_scheme_accepts_anything() =>
        Assert.Equal(VerifyResult.Valid, Signing.VerifyInbound(SignatureScheme.None, new Dictionary<string, string>(), Body, [], Now, Tolerance));

    [Theory]
    [InlineData(SignatureScheme.Timestamped)]
    [InlineData(SignatureScheme.HmacBody)]
    public void What_the_sample_signer_makes_the_verifier_accepts(SignatureScheme scheme)
    {
        var (name, value) = Signing.SignInbound(scheme, "abc", Now.ToUnixTimeSeconds(), Body);

        Assert.Equal(VerifyResult.Valid, Signing.VerifyInbound(scheme, new Dictionary<string, string> { [name] = value }, Body, ["abc"], Now, Tolerance));
    }
}

public class OutboundSigningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid EventId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string Sign(string secret = "k", Guid? id = null, long? t = null, string body = "{}") =>
        Signing.SignOutbound(secret, id ?? EventId, t ?? Now.ToUnixTimeSeconds(), body);

    [Fact]
    public void A_destination_can_verify_what_the_relay_signs() =>
        Assert.Equal(VerifyResult.Valid, Signing.VerifyOutbound(Sign(), EventId, "{}", "k", Now, TimeSpan.FromMinutes(5)));

    [Fact]
    public void The_event_id_is_part_of_the_signature_so_a_body_cannot_be_replayed_under_another_id() =>
        Assert.Equal(VerifyResult.Mismatch, Signing.VerifyOutbound(Sign(), Guid.NewGuid(), "{}", "k", Now, TimeSpan.FromMinutes(5)));

    [Fact]
    public void A_changed_body_or_secret_does_not_verify()
    {
        Assert.Equal(VerifyResult.Mismatch, Signing.VerifyOutbound(Sign(), EventId, "{\"x\":1}", "k", Now, TimeSpan.FromMinutes(5)));
        Assert.Equal(VerifyResult.Mismatch, Signing.VerifyOutbound(Sign(), EventId, "{}", "other", Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void An_old_signature_is_refused() =>
        Assert.Equal(VerifyResult.OutsideTolerance, Signing.VerifyOutbound(Sign(t: Now.ToUnixTimeSeconds() - 600), EventId, "{}", "k", Now, TimeSpan.FromMinutes(5)));

    [Theory]
    [InlineData(null, VerifyResult.Missing)]
    [InlineData("", VerifyResult.Missing)]
    [InlineData("nonsense", VerifyResult.Malformed)]
    [InlineData("t=1", VerifyResult.Malformed)]
    [InlineData("t=1,v1=zz", VerifyResult.Malformed)]
    public void Bad_headers_are_reported(string? header, VerifyResult expected) =>
        Assert.Equal(expected, Signing.VerifyOutbound(header, EventId, "{}", "k", Now, TimeSpan.FromMinutes(5)));

    [Fact]
    public void New_secrets_are_long_random_and_prefixed()
    {
        var a = Signing.NewSecret("whsec_");
        var b = Signing.NewSecret("whsec_");

        Assert.StartsWith("whsec_", a);
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40);
    }
}
