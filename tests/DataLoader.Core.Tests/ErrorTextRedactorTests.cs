using DataLoader.Core.Notifications;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Guards the one genuinely new secret-exposure surface this feature adds:
/// email leaves the machine, whereas the log file it is derived from does not.
///
/// <para>The two shapes below are not hypothetical. AGSI echoes the caller's
/// own x-key back inside the error body's message field, and OilX carries its
/// api_key in the query string — so both already appear in real ErrorMessage
/// text on this platform.</para>
/// </summary>
public class ErrorTextRedactorTests
{
    private const string Mask = "***REDACTED***";

    [Fact]
    public void Redacts_AgsiStyle_ApiKeyEchoedInErrorBody()
    {
        var input = "access denied (API key: abc123XYZ)";

        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain("abc123XYZ", result);
        Assert.Contains(Mask, result);
    }

    [Fact]
    public void Redacts_OilXStyle_ApiKeyInQueryString()
    {
        var input = "GET https://api.oilx.co/files?api_key=sk-9f3a2b&feed=Flow failed with 422";

        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain("sk-9f3a2b", result);
        Assert.Contains("https://api.oilx.co/files", result);
    }

    [Theory]
    [InlineData("apikey=topsecret", "topsecret")]
    [InlineData("api_key: topsecret", "topsecret")]
    [InlineData("x-key: topsecret", "topsecret")]
    [InlineData("password=topsecret", "topsecret")]
    [InlineData("Password: topsecret", "topsecret")]
    [InlineData("token=topsecret", "topsecret")]
    [InlineData("client_secret=topsecret", "topsecret")]
    public void Redacts_KnownKeyBearingTokens(string input, string secret)
    {
        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain(secret, result);
    }

    [Fact]
    public void LeavesOrdinaryErrorTextAlone()
    {
        // Over-redaction makes alerts useless, which is its own failure mode.
        var input = "Timeout expired after 300 seconds reading unit 2026-10-01 for Platts";

        Assert.Equal(input, ErrorTextRedactor.Redact(input));
    }

    [Fact]
    public void Truncate_CutsLongTextAndSaysSo()
    {
        var input = new string('x', 5000);

        var result = ErrorTextRedactor.Truncate(input, 2000);

        Assert.True(result.Length < 2100, $"expected ~2000 chars, got {result.Length}");
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void Truncate_LeavesShortTextUnchanged()
    {
        Assert.Equal("short", ErrorTextRedactor.Truncate("short", 2000));
    }

    [Fact]
    public void HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, ErrorTextRedactor.Redact(null));
        Assert.Equal(string.Empty, ErrorTextRedactor.Redact(""));
        Assert.Equal(string.Empty, ErrorTextRedactor.Truncate(null, 10));
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc-_123")]
    [InlineData("request failed with Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature attached")]
    [InlineData("bearer abc123DEF456")]
    public void Redacts_BearerTokens(string input)
    {
        // IIR and NUCORE authenticate with JWT bearer tokens, and a bearer value
        // carries no key= shape for the KeyValue pattern to catch.
        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", result);
        Assert.DoesNotContain("abc123DEF456", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    public void DoesNotRedactTheWordBearerOnItsOwn()
    {
        // Guard against over-redaction swallowing following prose.
        var input = "the bearer of this message is unknown";

        Assert.Equal(input, ErrorTextRedactor.Redact(input));
    }

    [Theory]
    [InlineData(@"{""error"":""unauthorized"",""api_key"":""sk-live-abc123XYZ""}", "sk-live-abc123XYZ")]
    [InlineData(@"{""user"":""svc"",""password"":""hunter2""}", "hunter2")]
    [InlineData(@"{""status"":401,""token"":""eyJhbGciOiJIUzI1NiJ9.payload.sig""}", "eyJhbGciOiJIUzI1NiJ9.payload.sig")]
    [InlineData(@"{""client_secret"": ""shh-9f3a""}", "shh-9f3a")]
    public void Redacts_SecretsInQuotedJsonFields(string input, string secret)
    {
        // Vendor error bodies are JSON. The quote between the field name and
        // the colon is what the original pattern could not cross.
        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain(secret, result);
        Assert.Contains("***REDACTED***", result);
    }

    [Theory]
    [InlineData("Bearer auth-header is missing")]
    [InlineData("the Bearer self-signed certificate was rejected")]
    [InlineData("Bearer realm.example.com rejected the request")]
    public void DoesNotRedactProseAfterBearer(string input)
    {
        // Over-redaction is its own failure mode: an alert nobody can act on.
        Assert.Equal(input, ErrorTextRedactor.Redact(input));
    }

    [Theory]
    [InlineData("An item with the same key has already been added. Key: 5")]
    [InlineData("Primary key: OrderId")]
    [InlineData("Cancellation token: expired after 300s")]
    public void DoesNotRedactDiagnosticProseUsingKeyOrToken(string input)
    {
        // The first of these is .NET's own Dictionary.Add exception text. This
        // platform has had duplicate-key incidents; masking the duplicate value
        // would destroy the one thing the alert needed to carry.
        Assert.Equal(input, ErrorTextRedactor.Redact(input));
    }

    [Fact]
    public void Truncate_NegativeMaxLength_DoesNotThrow()
    {
        Assert.Equal(string.Empty, ErrorTextRedactor.Truncate("anything", -1));
    }
}
