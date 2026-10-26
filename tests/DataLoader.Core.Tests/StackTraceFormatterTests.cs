using DataLoader.Core.Notifications;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// <see cref="StackTraceFormatter"/> is pure formatting — it trims framework
/// noise out of an exception's <c>ToString()</c> so a failure email shows the
/// loader's own call path. It is deliberately a separate class from
/// <see cref="ErrorTextRedactor"/>: these tests only cover what frames survive
/// and the length cap, never secret masking.
/// </summary>
public class StackTraceFormatterTests
{
    private const string Sample =
        "System.InvalidOperationException: boom\n" +
        "   at DataLoader.ICE.IceFeedReader.ReadAsync()\n" +
        "   at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)\n" +
        "   at Microsoft.Extensions.Http.Logging.LoggingHttpMessageHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\n" +
        "   at Polly.Retry.AsyncRetryEngine.ImplementationAsync(Context context)\n" +
        "   at DataLoader.Core.Pipeline.LoaderPipelineBase`3.ProcessOneAsync(TUnit unit, LoaderRunContext context, Counters counters, CancellationToken ct)";

    [Fact]
    public void DropsSystemMicrosoftAndPollyFrames_ButKeepsDataLoaderFrames()
    {
        var result = StackTraceFormatter.Format(Sample);

        Assert.Contains("DataLoader.ICE.IceFeedReader.ReadAsync", result);
        Assert.Contains("DataLoader.Core.Pipeline.LoaderPipelineBase", result);

        Assert.DoesNotContain("System.Runtime.CompilerServices.TaskAwaiter", result);
        Assert.DoesNotContain("Microsoft.Extensions.Http.Logging", result);
        Assert.DoesNotContain("Polly.Retry.AsyncRetryEngine", result);
    }

    [Fact]
    public void AlwaysKeepsTheExceptionTypeAndMessageLine()
    {
        var result = StackTraceFormatter.Format(Sample);

        Assert.Contains("System.InvalidOperationException: boom", result);
    }

    [Fact]
    public void KeepsInnerExceptionSeparatorsAndTheirMessages()
    {
        var withInner =
            "System.InvalidOperationException: outer boom ---> System.ArgumentException: inner boom\n" +
            "   at DataLoader.ICE.IceFeedReader.Inner()\n" +
            "   --- End of inner exception stack trace ---\n" +
            "   at DataLoader.ICE.IceFeedReader.Outer()";

        var result = StackTraceFormatter.Format(withInner);

        Assert.Contains("--->", result);
        Assert.Contains("inner boom", result);
        Assert.Contains("--- End of inner exception stack trace ---", result);
        Assert.Contains("DataLoader.ICE.IceFeedReader.Inner", result);
        Assert.Contains("DataLoader.ICE.IceFeedReader.Outer", result);
    }

    [Fact]
    public void FallsBackToUntrimmedText_WhenTrimmingWouldRemoveEveryFrame()
    {
        // Every single frame here is noisy. A trimmed result would have ZERO
        // frames, which reads as "the formatter broke this" — fall back
        // instead, even though that means showing noise.
        var allNoisy =
            "System.InvalidOperationException: boom\n" +
            "   at System.Net.Http.HttpClient.SendAsync(HttpRequestMessage request)\n" +
            "   at Microsoft.Extensions.Http.Logging.LoggingHttpMessageHandler.SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)\n" +
            "   at Polly.Retry.AsyncRetryEngine.ImplementationAsync(Context context)";

        var result = StackTraceFormatter.Format(allNoisy);

        Assert.Equal(allNoisy, result);
    }

    [Fact]
    public void CapsAt2000Characters()
    {
        var lines = new List<string> { "System.InvalidOperationException: boom" };
        for (var i = 0; i < 200; i++)
            lines.Add($"   at DataLoader.Vendor.Reader.Method{i}(int a, string b, double c)");
        var huge = string.Join("\n", lines);
        Assert.True(huge.Length > 2000, "test setup should produce more than 2000 chars");

        var result = StackTraceFormatter.Format(huge);

        Assert.True(result.Length <= 2000, $"result was {result.Length} chars");
    }

    [Fact]
    public void NullOrEmpty_BecomesEmptyString()
    {
        Assert.Equal(string.Empty, StackTraceFormatter.Format(null));
        Assert.Equal(string.Empty, StackTraceFormatter.Format(string.Empty));
    }

    [Fact]
    public void NoFramesAtAll_KeepsTheMessageLinesUntouched()
    {
        // An exception whose stack trace never got captured (thrown and
        // caught in the same frame before unwinding) has no "at " lines at
        // all — that is not "every frame dropped", it is "there were never
        // any frames", and the message must still come through.
        var noFrames = "System.InvalidOperationException: boom";

        var result = StackTraceFormatter.Format(noFrames);

        Assert.Equal(noFrames, result);
    }
}
