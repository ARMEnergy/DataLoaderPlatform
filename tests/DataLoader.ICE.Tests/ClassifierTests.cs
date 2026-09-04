using Xunit;

namespace DataLoader.ICE.Tests;

/// <summary>
/// The status matrix.
///
/// <para>
/// ICE answers HTTP 200 for real data, for a missing file, AND for expired
/// authentication (<c>docs/apis/ICE.md</c> 2), so these four outcomes are told apart
/// by content alone. Every body below is a real captured response, trimmed but with
/// its markers verbatim.
/// </para>
/// <para>
/// The pair that matters most is <c>NotAvailable</c> vs <c>AuthExpired</c>: the
/// first is a zero-row success, the second must fail. Confusing them means either
/// alarming on every weekend or silently recording an auth outage as a clean empty
/// day and then never revisiting it.
/// </para>
/// </summary>
public sealed class ClassifierTests
{
    private static readonly IceFeedDescriptor Gas = IceDescriptors.Find("IceGas")!;
    private static readonly IceFeedDescriptor Index = IceDescriptors.Find("CrudeIndex")!;
    private static readonly IceFeedDescriptor Ifll = IceDescriptors.Find("IfllOptions")!;

    [Fact]
    public void Real_data_is_classified_as_data()
    {
        var kind = IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.IceGas), Gas, out _);
        Assert.Equal(IceResponseKind.Data, kind);
    }

    [Fact]
    public void Missing_file_page_is_NotAvailable_not_an_error()
    {
        var kind = IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.NoFilesHtml), Gas, out var detail);

        Assert.Equal(IceResponseKind.NotAvailable, kind);
        Assert.Contains("no file published", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sso_login_page_is_AuthExpired_never_NotAvailable()
    {
        var kind = IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.SsoLoginHtml), Gas, out var detail);

        Assert.Equal(IceResponseKind.AuthExpired, kind);
        Assert.Contains("expired", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The regression this guards: an auth failure recorded as a clean zero-row day.
    /// The login page must never be mistaken for "no file today".
    /// </summary>
    [Fact]
    public void Auth_page_and_missing_page_are_never_confused()
    {
        var auth = IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.SsoLoginHtml), Gas, out _);
        var missing = IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.NoFilesHtml), Gas, out _);

        Assert.NotEqual(auth, missing);
        Assert.Equal(IceResponseKind.AuthExpired, auth);
        Assert.Equal(IceResponseKind.NotAvailable, missing);
    }

    /// <summary>
    /// A header-only file is REAL data with zero rows (the crude-index trades feed on
    /// a closed window is 178 bytes). It must not be reported as NotAvailable.
    /// </summary>
    [Fact]
    public void Header_only_file_is_data_not_NotAvailable()
    {
        var trades = IceDescriptors.Find("CrudeIndexTrades")!;
        var kind = IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.CrudeIndexTradesEmpty), trades, out _);

        Assert.Equal(IceResponseKind.Data, kind);
    }

    /// <summary>A changed upstream header must fail the file, not load it shifted.</summary>
    [Fact]
    public void Missing_required_header_is_Malformed()
    {
        const string renamed =
            "TRADE DATE|HUB|PRODUCT|STRIP|CONTRACT_CODE|CONTRACT TYPE|STRIKE|SETTLEMENT PRICE|NET CHANGE|EXPIRATION DATE|PRODUCT_ID\n" +
            "8/28/2026|AB-NIT|NG|9/1/2026|AEC|F||-1.9|-0.01|9/1/2026|451\n";

        var kind = IceResponseClassifier.Classify(TestHelpers.Bytes(renamed), Gas, out var detail);

        Assert.Equal(IceResponseKind.Malformed, kind);
        Assert.Contains("CONTRACT", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_body_is_Malformed()
    {
        var kind = IceResponseClassifier.Classify(Array.Empty<byte>(), Gas, out _);
        Assert.Equal(IceResponseKind.Malformed, kind);
    }

    [Fact]
    public void Quoted_csv_header_is_recognised()
    {
        var kind = IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.CrudeIndex), Index, out _);
        Assert.Equal(IceResponseKind.Data, kind);
    }

    /// <summary>An XLSX feed accepts the zip magic and nothing else.</summary>
    [Fact]
    public void Xlsx_zip_magic_is_data()
    {
        var zip = XlsxBuilder.Build(
            new[] { "TRADE DATE", "CONTRACT", "STRIP", "EXPIRATION_DATE", "STRIKE", "PUT/CALL",
                    "SETTLEMENT_PRICE", "VOLATILITY", "DELTA", "GAMMA", "THETA", "VEGA" },
            Array.Empty<string?[]>());

        var kind = IceResponseClassifier.Classify(zip, Ifll, out _);
        Assert.Equal(IceResponseKind.Data, kind);
    }

    [Fact]
    public void Non_zip_body_for_an_xlsx_feed_is_Malformed()
    {
        var kind = IceResponseClassifier.Classify(TestHelpers.Bytes("TRADE DATE|CONTRACT\n"), Ifll, out _);
        Assert.Equal(IceResponseKind.Malformed, kind);
    }

    /// <summary>
    /// An HTML page served where an XLSX was expected is caught as a sentinel, not
    /// handed to the zip reader to throw on.
    /// </summary>
    [Fact]
    public void Html_served_for_an_xlsx_feed_is_still_a_sentinel()
    {
        Assert.Equal(IceResponseKind.NotAvailable,
            IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.NoFilesHtml), Ifll, out _));

        Assert.Equal(IceResponseKind.AuthExpired,
            IceResponseClassifier.Classify(TestHelpers.Bytes(Samples.SsoLoginHtml), Ifll, out _));
    }

    /// <summary>
    /// An unrecognised HTML interstitial is treated as an auth problem so it gets one
    /// re-auth and retry, rather than failing the unit outright on the first sight.
    /// </summary>
    [Fact]
    public void Unknown_html_is_treated_as_auth_so_it_gets_one_retry()
    {
        var kind = IceResponseClassifier.Classify(
            TestHelpers.Bytes("<!DOCTYPE html>\n<html><body>Service temporarily unavailable</body></html>"),
            Gas, out _);

        Assert.Equal(IceResponseKind.AuthExpired, kind);
    }

    /// <summary>Binary that is neither zip nor text must not throw during the sniff.</summary>
    [Fact]
    public void Arbitrary_binary_does_not_throw()
    {
        var noise = new byte[512];
        new Random(1234).NextBytes(noise);

        var exception = Record.Exception(() => IceResponseClassifier.Classify(noise, Gas, out _));
        Assert.Null(exception);
    }
}
