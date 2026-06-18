using System.Globalization;

internal static class CastFormat
{
    internal const int IntervalDecimalPlaces = 3;
    private const double IntervalScale = 1000.0;
    internal const double ExitEventGapSeconds = 0.05;

    internal static List<double> ToRelativeIntervals(IReadOnlyList<double> absoluteTimes)
    {
        if (absoluteTimes.Count == 0)
            return [];

        var intervals = new List<double>(absoluteTimes.Count);
        var error = 0.0;
        var previous = 0.0;

        for (var i = 0; i < absoluteTimes.Count; i++)
        {
            var exact = i == 0 ? absoluteTimes[i] : absoluteTimes[i] - previous;
            previous = absoluteTimes[i];

            var scaled = exact * IntervalScale + error;
            var quantized = Math.Round(scaled, MidpointRounding.AwayFromZero);
            error = scaled - quantized;
            intervals.Add(quantized / IntervalScale);
        }

        return intervals;
    }

    internal static List<double> ToAbsoluteTimes(IReadOnlyList<double> intervals)
    {
        var absolute = new List<double>(intervals.Count);
        var time = 0.0;
        foreach (var interval in intervals)
        {
            time += interval;
            absolute.Add(time);
        }

        return absolute;
    }

    internal static string FormatInterval(double interval) =>
        interval.ToString($"0.{new string('0', IntervalDecimalPlaces)}", CultureInfo.InvariantCulture);
}
