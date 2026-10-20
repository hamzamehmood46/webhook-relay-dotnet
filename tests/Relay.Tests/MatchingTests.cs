using System.Text.Json;
using Relay.Engine;

namespace Relay.Tests;

public class MatchingTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("invoice.paid", "invoice.paid", true)]
    [InlineData("invoice.paid", "Invoice.Paid", true)]
    [InlineData("invoice.paid", "invoice.failed", false)]
    [InlineData("invoice.*", "invoice.paid", true)]
    [InlineData("invoice.*", "invoice.payment.failed", true)]
    [InlineData("invoice.*", "invoices.paid", false)]
    [InlineData("invoice.*", "invoice", false)]
    [InlineData("invoice.*", "invoice.", false)]
    [InlineData("*", "anything.at.all", true)]
    [InlineData("*", "unknown", true)]
    [InlineData("customer.created", "customer.created.extra", false)]
    public void An_event_type_matches_an_exact_name_a_prefix_or_everything(string pattern, string type, bool expected) =>
        Assert.Equal(expected, Matching.TypeMatches([pattern], type));

    [Fact]
    public void Several_patterns_match_if_any_does()
    {
        Assert.True(Matching.TypeMatches(["customer.*", "invoice.paid"], "invoice.paid"));
        Assert.True(Matching.TypeMatches(["customer.*", "invoice.paid"], "customer.deleted"));
        Assert.False(Matching.TypeMatches(["customer.*", "invoice.paid"], "invoice.failed"));
        Assert.False(Matching.TypeMatches([], "invoice.paid"));
    }

    [Fact]
    public void A_value_is_read_from_a_dotted_path()
    {
        var body = Json("{\"data\":{\"object\":{\"id\":\"ch_1\",\"amount\":4200,\"paid\":true,\"refunded\":false}}}");

        Assert.Equal("ch_1", Matching.ReadPath(body, "data.object.id"));
        Assert.Equal("4200", Matching.ReadPath(body, "data.object.amount"));
        Assert.Equal("true", Matching.ReadPath(body, "data.object.paid"));
        Assert.Equal("false", Matching.ReadPath(body, "data.object.refunded"));
    }

    [Theory]
    [InlineData("data.missing")]
    [InlineData("missing.object.id")]
    [InlineData("data.object")]
    [InlineData("data.object.id.deeper")]
    [InlineData("")]
    public void Anything_missing_or_not_a_plain_value_reads_as_null(string path)
    {
        var body = Json("{\"data\":{\"object\":{\"id\":\"ch_1\"}}}");

        Assert.Null(Matching.ReadPath(body, path));
    }

    [Fact]
    public void A_null_or_array_value_reads_as_null() =>
        Assert.Null(Matching.ReadPath(Json("{\"a\":null,\"b\":[1,2]}"), "a") ?? Matching.ReadPath(Json("{\"a\":null,\"b\":[1,2]}"), "b"));

    [Fact]
    public void The_event_id_and_type_can_come_from_headers()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-GitHub-Event"] = " push ", ["X-GitHub-Delivery"] = "d-1" };

        Assert.Equal("push", Matching.Extract("header:X-GitHub-Event", headers, Json("{}")));
        Assert.Equal("d-1", Matching.Extract("header:x-github-delivery", headers, Json("{}")));
        Assert.Null(Matching.Extract("header:X-Nope", headers, Json("{}")));
    }

    [Fact]
    public void Without_the_header_prefix_the_path_reads_the_body()
    {
        var headers = new Dictionary<string, string>();

        Assert.Equal("evt_9", Matching.Extract("id", headers, Json("{\"id\":\"evt_9\"}")));
        Assert.Null(Matching.Extract("id", headers, Json("{\"id\":\"  \"}")));
    }

    [Fact]
    public void No_condition_always_matches() => Assert.True(Matching.ConditionMatches(null, Json("{}")));

    [Fact]
    public void A_condition_needs_the_exact_value_at_the_path()
    {
        var body = Json("{\"data\":{\"currency\":\"usd\",\"amount\":4200}}");

        Assert.True(Matching.ConditionMatches(new("data.currency", "usd"), body));
        Assert.False(Matching.ConditionMatches(new("data.currency", "USD"), body));
        Assert.False(Matching.ConditionMatches(new("data.currency", "eur"), body));
        Assert.True(Matching.ConditionMatches(new("data.amount", "4200"), body));
        Assert.False(Matching.ConditionMatches(new("data.region", "us"), body));
    }
}
