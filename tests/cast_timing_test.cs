#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:include ../CastTimingPipeline.cs

var failures = 0;
failures += Run("CollapseIdleGapLong", CollapseIdleGapLong);
failures += Run("PreserveShortGap", PreserveShortGap);
failures += Run("LinearCompressScales", LinearCompressScales);
failures += Run("LinearCompressUnderMax", LinearCompressUnderMax);
failures += Run("AdjustTimelineCombined", AdjustTimelineCombined);
failures += Run("CoalesceTimedTextMerges", CoalesceTimedTextMerges);
failures += Run("CoalesceTimedUtf8Merges", CoalesceTimedUtf8Merges);
failures += Run("HighlightSplitterPreservesSegments", HighlightSplitterPreservesSegments);

return failures == 0 ? 0 : 1;

static int Run(string name, Func<bool> test)
{
    try
    {
        if (test())
        {
            Console.Error.WriteLine($"ok {name}");
            return 0;
        }

        Console.Error.WriteLine($"FAIL {name}");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
        return 1;
    }
}

static void CollapseIdleGaps(Span<double> times, double silenceThreshold, double maxIdle)
{
    if (times.Length <= 1 || silenceThreshold < 0 || maxIdle < 0)
        return;

    var original = times.ToArray();
    var cursor = original[0];
    times[0] = cursor;
    for (var i = 1; i < original.Length; i++)
    {
        var gap = original[i] - original[i - 1];
        if (gap > silenceThreshold)
            gap = maxIdle;
        cursor += gap;
        times[i] = cursor;
    }
}

static void LinearCompress(Span<double> times, double maxDuration)
{
    if (times.Length == 0)
        return;

    var total = times[^1];
    if (total <= maxDuration || total <= 0)
        return;

    var scale = maxDuration / total;
    for (var i = 0; i < times.Length; i++)
        times[i] *= scale;
}

static bool CollapseIdleGapLong()
{
    var times = new[] { 0.0, 30.0, 30.5 };
    CollapseIdleGaps(times, silenceThreshold: 2.0, maxIdle: 0.2);
    return Math.Abs(times[1] - 0.2) < 1e-9 && Math.Abs(times[2] - 0.7) < 1e-9;
}

static bool PreserveShortGap()
{
    var times = new[] { 0.0, 0.5, 1.0 };
    CollapseIdleGaps(times, silenceThreshold: 2.0, maxIdle: 0.2);
    return Math.Abs(times[1] - 0.5) < 1e-9 && Math.Abs(times[2] - 1.0) < 1e-9;
}

static bool LinearCompressScales()
{
    var times = new[] { 0.0, 5.0, 10.0 };
    LinearCompress(times, maxDuration: 5.0);
    return Math.Abs(times[0]) < 1e-9
        && Math.Abs(times[1] - 2.5) < 1e-9
        && Math.Abs(times[2] - 5.0) < 1e-9;
}

static bool LinearCompressUnderMax()
{
    var times = new[] { 0.0, 1.0, 2.0 };
    LinearCompress(times, maxDuration: 5.0);
    return Math.Abs(times[1] - 1.0) < 1e-9 && Math.Abs(times[2] - 2.0) < 1e-9;
}

static bool AdjustTimelineCombined()
{
    var times = Enumerable.Range(0, 201).Select(static i => i * 0.5).ToArray();
    var adjusted = CastTimingPipeline.AdjustTimeline(times, new CastTimingSettings(2.0, 0.2, 10.0));
    return Math.Abs(adjusted[^1] - 10.0) < 1e-9;
}

static bool CoalesceTimedTextMerges()
{
    var coalesced = CastTimingPipeline.CoalesceTimedText(
    [
        (0.1, "a"),
        (0.1, "b"),
        (0.2, "c"),
    ]);
    return coalesced.Count == 2
        && coalesced[0].Text == "ab"
        && coalesced[1].Text == "c";
}

static bool CoalesceTimedUtf8Merges()
{
    var coalesced = CastTimingPipeline.CoalesceTimedUtf8(
    [
        (0.1, (ReadOnlyMemory<byte>)new byte[] { (byte)'a' }),
        (0.1, (ReadOnlyMemory<byte>)new byte[] { (byte)'b' }),
        (0.2, (ReadOnlyMemory<byte>)new byte[] { (byte)'c' }),
    ]);
    return coalesced.Count == 2
        && coalesced[0].Data.Length == 2
        && coalesced[0].Data[0] == (byte)'a'
        && coalesced[0].Data[1] == (byte)'b'
        && coalesced[1].Data[0] == (byte)'c';
}

static bool HighlightSplitterPreservesSegments()
{
    var highlighted = "\u001b[31mhello\u001b[0m\nworld";
    var split = HighlightSplitter.SplitByRawSegments(highlighted, ["hello" + "\n", "world"]);
    return split.Count == 2
        && split[0].Contains("hello")
        && split[1].Contains("world");
}
