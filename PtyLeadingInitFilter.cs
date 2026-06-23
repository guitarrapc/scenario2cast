using System.Text;

internal sealed class PtyLeadingInitFilter
{
    private const int LeadingByteLimit = 4096;
    private const int CleanupByteLimit = 4096;
    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;

    private readonly List<byte> pending = [];
    private FilterMode mode = FilterMode.LeadingInit;
    private int leadingBytesSeen;
    private int cleanupBytesSeen;

    internal int StrippedByteCount { get; private set; }

    internal ReadOnlyMemory<byte> Process(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty)
            return ReadOnlyMemory<byte>.Empty;

        AppendPending(input.Span);

        var output = new List<byte>(input.Length);
        while (pending.Count > 0)
        {
            var progressed = mode switch
            {
                FilterMode.LeadingInit => ProcessLeadingInit(output, out _),
                FilterMode.Passthrough => ProcessPassthrough(output, out _),
                FilterMode.AfterAlternateScreenExit => ProcessAfterAlternateScreenExit(output, out _),
                _ => false,
            };

            if (!progressed)
                break;
        }

        return output.Count == 0 ? ReadOnlyMemory<byte>.Empty : output.ToArray();
    }

    internal ReadOnlyMemory<byte> Complete()
    {
        if (pending.Count == 0)
            return ReadOnlyMemory<byte>.Empty;

        var output = pending.ToArray();
        pending.Clear();
        mode = FilterMode.Passthrough;
        return output;
    }

    private bool ProcessLeadingInit(List<byte> output, out bool incomplete)
    {
        incomplete = false;
        var first = pending[0];
        if (IsPrintableUtf8Start(first))
        {
            mode = FilterMode.Passthrough;
            AppendAndClear(output, pending);
            return true;
        }

        if (first == Esc)
        {
            if (!TryConsumeEscape(output, EscapeContext.LeadingInit, out incomplete))
            {
                if (incomplete && leadingBytesSeen + pending.Count > LeadingByteLimit)
                {
                    mode = FilterMode.Passthrough;
                    AppendAndClear(output, pending);
                    return true;
                }

                return false;
            }
        }
        else
        {
            output.Add(first);
            pending.RemoveAt(0);
        }

        leadingBytesSeen++;
        if (mode == FilterMode.LeadingInit && leadingBytesSeen > LeadingByteLimit)
        {
            mode = FilterMode.Passthrough;
            AppendAndClear(output, pending);
        }

        return true;
    }

    private bool ProcessPassthrough(List<byte> output, out bool incomplete)
    {
        incomplete = false;

        var escIndex = pending.IndexOf(Esc);
        if (escIndex < 0)
        {
            AppendAndClear(output, pending);
            return true;
        }

        if (escIndex > 0)
        {
            AppendRange(output, pending, 0, escIndex);
            pending.RemoveRange(0, escIndex);
            return true;
        }

        return TryConsumeEscape(output, EscapeContext.Passthrough, out incomplete);
    }

    private bool ProcessAfterAlternateScreenExit(List<byte> output, out bool incomplete)
    {
        incomplete = false;
        var first = pending[0];
        if (IsPrintableUtf8Start(first))
        {
            mode = FilterMode.Passthrough;
            AppendAndClear(output, pending);
            return true;
        }

        if (first == Esc)
        {
            if (!TryConsumeEscape(output, EscapeContext.AfterAlternateScreenExit, out incomplete))
            {
                if (incomplete && cleanupBytesSeen + pending.Count > CleanupByteLimit)
                {
                    mode = FilterMode.Passthrough;
                    AppendAndClear(output, pending);
                    return true;
                }

                return false;
            }
        }
        else if (first is (byte)'\r' or (byte)'\n' or (byte)'\t')
        {
            StripPending(1);
        }
        else
        {
            output.Add(first);
            pending.RemoveAt(0);
        }

        cleanupBytesSeen++;
        if (mode == FilterMode.AfterAlternateScreenExit && cleanupBytesSeen > CleanupByteLimit)
        {
            mode = FilterMode.Passthrough;
            AppendAndClear(output, pending);
        }

        return true;
    }

    private void AppendPending(ReadOnlySpan<byte> input)
    {
        pending.EnsureCapacity(pending.Count + input.Length);
        foreach (var value in input)
            pending.Add(value);
    }

    private bool TryConsumeEscape(List<byte> output, EscapeContext context, out bool incomplete)
    {
        incomplete = false;
        if (pending.Count < 2)
        {
            incomplete = true;
            return false;
        }

        return pending[1] switch
        {
            (byte)'[' => TryConsumeCsi(output, context, out incomplete),
            (byte)']' => TryConsumeOsc(output, context, out incomplete),
            _ => ConsumeUnknownEscape(output),
        };
    }

    private bool TryConsumeCsi(List<byte> output, EscapeContext context, out bool incomplete)
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

        if (IsAlternateScreenMode(finalByte, parameters, out var alternateEnabled))
        {
            AppendRange(output, pending, 0, length);
            pending.RemoveRange(0, length);
            if (alternateEnabled)
            {
                mode = FilterMode.Passthrough;
            }
            else
            {
                mode = FilterMode.AfterAlternateScreenExit;
                cleanupBytesSeen = 0;
            }
            return true;
        }

        if (ShouldStripCsi(finalByte, parameters, context))
        {
            StrippedByteCount += length;
            pending.RemoveRange(0, length);
            return true;
        }

        if (context == EscapeContext.AfterAlternateScreenExit)
            mode = FilterMode.Passthrough;

        AppendRange(output, pending, 0, length);
        pending.RemoveRange(0, length);
        return true;
    }

    private bool TryConsumeOsc(List<byte> output, EscapeContext context, out bool incomplete)
    {
        incomplete = false;
        for (var i = 2; i < pending.Count; i++)
        {
            if (pending[i] == Bel)
            {
                if (context == EscapeContext.Passthrough)
                {
                    AppendRange(output, pending, 0, i + 1);
                    pending.RemoveRange(0, i + 1);
                    return true;
                }

                StripPending(i + 1);
                return true;
            }

            if (pending[i] == Esc && i + 1 < pending.Count && pending[i + 1] == (byte)'\\')
            {
                if (context == EscapeContext.Passthrough)
                {
                    AppendRange(output, pending, 0, i + 2);
                    pending.RemoveRange(0, i + 2);
                    return true;
                }

                StripPending(i + 2);
                return true;
            }
        }

        incomplete = true;
        return false;
    }

    private bool ConsumeUnknownEscape(List<byte> output)
    {
        if (mode == FilterMode.AfterAlternateScreenExit)
            mode = FilterMode.Passthrough;

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

    private static bool ShouldStripCsi(byte finalByte, string parameters, EscapeContext context)
    {
        if (context == EscapeContext.Passthrough)
            return false;

        if (finalByte == (byte)'J' && parameters is "" or "0" or "1" or "2")
            return true;

        if (context == EscapeContext.AfterAlternateScreenExit && finalByte == (byte)'K')
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

    private static bool IsAlternateScreenMode(byte finalByte, string parameters, out bool enabled)
    {
        enabled = finalByte == (byte)'h';
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

    private enum FilterMode
    {
        LeadingInit,
        Passthrough,
        AfterAlternateScreenExit,
    }

    private enum EscapeContext
    {
        LeadingInit,
        Passthrough,
        AfterAlternateScreenExit,
    }
}
