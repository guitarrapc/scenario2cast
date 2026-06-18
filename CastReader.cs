using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class CastReadException : Exception
{
    public CastReadException(string message) : base(message) { }
}

internal readonly record struct CastRecording(
    int Width,
    int Height,
    ResolvedRenderSettings RenderSettings,
    List<CastEvent> Events);

internal static class CastReader
{
    private static readonly Regex FontSizeTagRegex = new(
        "^s2c:font-size=(\\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static CastRecording Read(string castPath)
    {
        var lines = File.ReadAllLines(castPath, Encoding.UTF8);
        if (lines.Length == 0)
            throw new CastReadException("cast file is empty");

        var headerLine = lines[0].Trim();
        if (headerLine.Length == 0)
            throw new CastReadException("cast header is missing");

        if (headerLine.StartsWith('#'))
            throw new CastReadException("cast header is missing");

        using var headerDoc = ParseJsonOrThrow(headerLine, 1);
        var header = headerDoc.RootElement;

        if (!header.TryGetProperty("version", out var versionElement) ||
            !versionElement.TryGetInt32(out var version) ||
            version is not (2 or 3))
        {
            throw new CastReadException("cast version must be 2 or 3");
        }

        if (!TryReadTerminalSize(header, version, out var width, out var height))
            throw new CastReadException("cast header is missing terminal size");

        if (!RenderSettingsResolver.IsValidTerminalSize(width, height))
            throw new CastReadException(
                $"cast terminal size must be {RenderSettingsResolver.MinTerminalCols}–{RenderSettingsResolver.MaxTerminalCols}");

        var renderSettings = ResolveFromCastHeader(header, version);
        var events = new List<CastEvent>();
        var warnedCodes = new HashSet<string>(StringComparer.Ordinal);
        var usesRelativeTime = version == 3;
        var absoluteTime = 0.0;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('#'))
                continue;

            if (!TryParseEventLine(line, i + 1, warnedCodes, out var ev))
                throw new CastReadException($"invalid cast event at line {i + 1}");

            if (!ev.HasValue)
                continue;

            var parsed = ev.Value;
            if (usesRelativeTime)
            {
                absoluteTime += parsed.Time;
                parsed = parsed with { Time = absoluteTime };
            }

            events.Add(parsed);
        }

        return new CastRecording(width, height, renderSettings, events);
    }

    internal static ResolvedRenderSettings ResolveFromCastHeader(JsonElement header, int version)
    {
        var fontSize = RenderSettingsResolver.DefaultFontSize;
        if (version == 3)
            fontSize = TryParseFontSizeFromTags(header) ?? fontSize;

        var themeElement = version == 3
            ? header.TryGetProperty("term", out var term) &&
              term.ValueKind == JsonValueKind.Object &&
              term.TryGetProperty("theme", out var termTheme)
                ? termTheme
                : default
            : header.TryGetProperty("theme", out var topTheme)
                ? topTheme
                : default;

        var (fg, bg, palette) = TryParseTheme(themeElement);
        return new ResolvedRenderSettings(fontSize, new ResolvedTheme(fg, bg, palette));
    }

    private static bool TryReadTerminalSize(JsonElement header, int version, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (version == 3)
        {
            if (!header.TryGetProperty("term", out var term) || term.ValueKind != JsonValueKind.Object)
                return false;

            return TryReadPositiveInt(term, "cols", out width) &&
                   TryReadPositiveInt(term, "rows", out height);
        }

        return TryReadPositiveInt(header, "width", out width) &&
               TryReadPositiveInt(header, "height", out height);
    }

    private static int? TryParseFontSizeFromTags(JsonElement header)
    {
        if (!header.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var tag in tags.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String)
                continue;

            var value = tag.GetString();
            if (value is null)
                continue;

            var match = FontSizeTagRegex.Match(value);
            if (!match.Success)
                continue;

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                continue;

            if (parsed is >= RenderSettingsResolver.MinFontSize and <= RenderSettingsResolver.MaxFontSize)
                return parsed;
        }

        return null;
    }

    private static (string fg, string bg, string palette) TryParseTheme(JsonElement theme)
    {
        string fg = RenderSettingsResolver.DefaultFg;
        string bg = RenderSettingsResolver.DefaultBg;
        string palette = RenderSettingsResolver.DefaultPalette;

        if (theme.ValueKind != JsonValueKind.Object)
            return (fg, bg, palette);

        if (theme.TryGetProperty("fg", out var fgElement) &&
            fgElement.ValueKind == JsonValueKind.String &&
            TryParseHexColor(fgElement.GetString(), out var parsedFg))
        {
            fg = parsedFg;
        }

        if (theme.TryGetProperty("bg", out var bgElement) &&
            bgElement.ValueKind == JsonValueKind.String &&
            TryParseHexColor(bgElement.GetString(), out var parsedBg))
        {
            bg = parsedBg;
        }

        if (theme.TryGetProperty("palette", out var paletteElement) &&
            paletteElement.ValueKind == JsonValueKind.String &&
            TryParsePalette(paletteElement.GetString(), out var parsedPalette))
        {
            palette = parsedPalette;
        }

        return (fg, bg, palette);
    }

    private static bool TryReadPositiveInt(JsonElement header, string name, out int value)
    {
        value = 0;
        if (!header.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number)
            return false;

        return element.TryGetInt32(out value) && value > 0;
    }

    private static JsonDocument ParseJsonOrThrow(string json, int lineNumber)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CastReadException($"invalid JSON at line {lineNumber}: {ex.Message}");
        }
    }

    private static bool TryParseHexColor(string? value, out string color)
    {
        color = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        if (value.Length is not (4 or 7) || value[0] != '#')
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (!IsHexDigit(value[i]))
                return false;
        }

        color = value;
        return true;
    }

    private static bool TryParsePalette(string? value, out string palette)
    {
        palette = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 16)
            return false;

        var colors = new string[16];
        for (var i = 0; i < 16; i++)
        {
            if (!TryParseHexColor(parts[i], out colors[i]))
                return false;
        }

        palette = string.Join(':', colors);
        return true;
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool TryParseEventLine(
        string line,
        int lineNumber,
        HashSet<string> warnedCodes,
        out CastEvent? ev)
    {
        ev = null;

        using var doc = ParseJsonOrThrow(line, lineNumber);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3)
            return false;

        if (root[0].ValueKind != JsonValueKind.Number)
            return false;

        if (root[1].ValueKind != JsonValueKind.String)
            return false;

        if (root[2].ValueKind != JsonValueKind.String)
            return false;

        var code = root[1].GetString() ?? "";
        var time = root[0].GetDouble();
        var data = root[2].GetString() ?? "";

        if (string.Equals(code, "o", StringComparison.Ordinal))
        {
            ev = CastEvent.Output(time, data);
            return true;
        }

        if (string.Equals(code, "r", StringComparison.Ordinal))
        {
            if (!TryParseResizeData(data, out var resizeWidth, out var resizeHeight))
            {
                if (warnedCodes.Add("invalid-resize"))
                    Console.Error.WriteLine("Warning: svg: invalid resize event data; skipping");

                return true;
            }

            ev = CastEvent.Resize(time, resizeWidth, resizeHeight);
            return true;
        }

        if (string.Equals(code, "m", StringComparison.Ordinal) ||
            string.Equals(code, "x", StringComparison.Ordinal) ||
            string.Equals(code, "i", StringComparison.Ordinal))
        {
            return true;
        }

        if (warnedCodes.Add(code))
            Console.Error.WriteLine($"Warning: svg: unsupported cast event code '{code}'; skipping");

        return true;
    }

    private static bool TryParseResizeData(string data, out int width, out int height)
    {
        width = 0;
        height = 0;
        var separator = data.IndexOf('x');
        if (separator <= 0 || separator >= data.Length - 1)
            return false;

        if (!int.TryParse(data.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out width))
            return false;

        if (!int.TryParse(data.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            return false;

        return RenderSettingsResolver.IsValidTerminalSize(width, height);
    }
}
