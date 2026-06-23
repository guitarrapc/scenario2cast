using System.Text;

internal sealed class PtyLeadingInitFilter
{
    private const int LeadingByteLimit = 4096;
    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;

    private readonly List<byte> pending = [];
    private bool filtering = true;
    private int leadingBytesSeen;

    internal int StrippedByteCount { get; private set; }

    internal ReadOnlyMemory<byte> Process(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
            return ReadOnlyMemory<byte>.Empty;

        if (!filtering)
            return input;

        AppendPending(input.Span);
        leadingBytesSeen += input.Length;

        var output = new List<byte>(input.Length);
        while (pending.Count > 0 && filtering)
        {
            var first = pending[0];
            if (IsPrintableUtf8Start(first))
            {
                filtering = false;
                AppendAndClear(output, pending);
                break;
            }

            if (first == Esc)
            {
                if (!TryConsumeEscape(output, out var incomplete))
                    break;

                if (incomplete)
                    break;
            }
            else
            {
                output.Add(first);
                pending.RemoveAt(0);
            }

            if (filtering && leadingBytesSeen > LeadingByteLimit)
            {
                filtering = false;
                AppendAndClear(output, pending);
                break;
            }
        }

        return output.Count == 0 ? ReadOnlyMemory<byte>.Empty : output.ToArray();
    }

    internal ReadOnlyMemory<byte> Complete()
    {
        if (pending.Count == 0)
            return ReadOnlyMemory<byte>.Empty;

        var output = pending.ToArray();
        pending.Clear();
        filtering = false;
        return output;
    }

    private void AppendPending(ReadOnlySpan<byte> input)
    {
        pending.EnsureCapacity(pending.Count + input.Length);
        foreach (var value in input)
            pending.Add(value);
    }

    private bool TryConsumeEscape(List<byte> output, out bool incomplete)
    {
        incomplete = false;
        if (pending.Count < 2)
        {
            incomplete = true;
            return false;
        }

        return pending[1] switch
        {
            (byte)'[' => TryConsumeCsi(output, out incomplete),
            (byte)']' => TryConsumeOsc(out incomplete),
            _ => ConsumeUnknownEscape(output),
        };
    }

    private bool TryConsumeCsi(List<byte> output, out bool incomplete)
    {
        incomplete = false;
        var finalIndex = -1;
        for (var i = 2; i < pending.Count; i++)
        {
            var value = pending[i];
            if (value is >= 0x40 and <= 0x7E)
            {
                finalIndex = i;
                break;
            }
        }

        if (finalIndex < 0)
        {
            incomplete = true;
            return false;
        }

        var finalByte = pending[finalIndex];
        var parameters = GetCsiParameters(finalIndex);
        var length = finalIndex + 1;

        if (IsAlternateScreenMode(finalByte, parameters))
        {
            filtering = false;
            AppendAndClear(output, pending);
            return true;
        }

        if (ShouldStripCsi(finalByte, parameters))
        {
            StrippedByteCount += length;
            pending.RemoveRange(0, length);
            return true;
        }

        AppendRange(output, pending, 0, length);
        pending.RemoveRange(0, length);
        return true;
    }

    private bool TryConsumeOsc(out bool incomplete)
    {
        incomplete = false;
        for (var i = 2; i < pending.Count; i++)
        {
            if (pending[i] == Bel)
            {
                StripPending(i + 1);
                return true;
            }

            if (pending[i] == Esc && i + 1 < pending.Count && pending[i + 1] == (byte)'\\')
            {
                StripPending(i + 2);
                return true;
            }
        }

        incomplete = true;
        return false;
    }

    private bool ConsumeUnknownEscape(List<byte> output)
    {
        output.Add(pending[0]);
        output.Add(pending[1]);
        pending.RemoveRange(0, 2);
        return true;
    }

    private string GetCsiParameters(int finalIndex)
    {
        var count = 0;
        for (var i = 2; i < finalIndex; i++)
        {
            if (pending[i] is >= 0x30 and <= 0x3F)
                count++;
        }

        if (count == 0)
            return "";

        Span<byte> bytes = count <= 64 ? stackalloc byte[count] : new byte[count];
        var offset = 0;
        for (var i = 2; i < finalIndex; i++)
        {
            if (pending[i] is >= 0x30 and <= 0x3F)
                bytes[offset++] = pending[i];
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static bool ShouldStripCsi(byte finalByte, string parameters)
    {
        if (finalByte == (byte)'J' && parameters is "" or "0" or "1" or "2")
            return true;

        if ((finalByte == (byte)'H' || finalByte == (byte)'f') && !parameters.StartsWith("?", StringComparison.Ordinal))
            return true;

        if (finalByte == (byte)'m' && parameters is "" or "0")
            return true;

        return IsStripPrivateMode(finalByte, parameters);
    }

    private static bool IsStripPrivateMode(byte finalByte, string parameters)
    {
        if (finalByte is not ((byte)'h' or (byte)'l') || !parameters.StartsWith("?", StringComparison.Ordinal))
            return false;

        var modes = parameters[1..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return modes.Length > 0 && modes.All(static mode => mode is "25" or "9001" or "1004" or "2004");
    }

    private static bool IsAlternateScreenMode(byte finalByte, string parameters)
    {
        if (finalByte is not ((byte)'h' or (byte)'l') || !parameters.StartsWith("?", StringComparison.Ordinal))
            return false;

        var modes = parameters[1..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return modes.Any(static mode => mode is "1049" or "47" or "1047");
    }

    private void StripPending(int length)
    {
        StrippedByteCount += length;
        pending.RemoveRange(0, length);
    }

    private static bool IsPrintableUtf8Start(byte value) =>
        value != Esc && value != 0x7F && (value >= 0x20 || value >= 0x80);

    private static void AppendAndClear(List<byte> output, List<byte> source)
    {
        AppendRange(output, source, 0, source.Count);
        source.Clear();
    }

    private static void AppendRange(List<byte> output, List<byte> source, int start, int length)
    {
        output.EnsureCapacity(output.Count + length);
        for (var i = 0; i < length; i++)
            output.Add(source[start + i]);
    }
}
