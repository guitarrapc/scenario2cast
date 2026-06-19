#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable

// Build samples/matrix.cast — animated matrix rain without per-frame command typing.
//
// Usage (from repository root):
//   dotnet run samples/matrix-gen.cs

using System.Globalization;
using System.Text;

const int Width = 80;
const int Height = 20;
const int Fps = 12;
const int FrameCount = 72;
const double FrameSeconds = 1.0 / Fps;

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
var outputPath = Path.Combine(repoRoot, "samples", "matrix.cast");

const string MatrixCharPool = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789@#$%&*+=<>";

var columns = CreateColumns(Width, seed: 42);
var rng = new Random(42);
var events = new List<(double Time, string Data)>(FrameCount + 1);

var t = 0.0;
events.Add((t, "\u001b[?1049h" + BuildFrame(columns, rng)));

for (var frame = 1; frame < FrameCount; frame++)
{
    t += FrameSeconds;
    events.Add((t, BuildFrame(columns, rng)));
}

WriteCast(outputPath, events);
Console.Error.WriteLine($"Written: {outputPath}  ({events.Count} frames, {t.ToString("0.0", CultureInfo.InvariantCulture)}s)");
return 0;

static ColumnState[] CreateColumns(int width, int seed)
{
    var rng = new Random(seed);
    var columns = new ColumnState[width];
    for (var col = 0; col < width; col++)
    {
        columns[col] = new ColumnState
        {
            Y = rng.Next(-14, Height),
            Speed = 1 + rng.Next(2),
            Char = MatrixCharPool[rng.Next(MatrixCharPool.Length)],
            Tick = rng.Next(4, 14),
        };
    }

    return columns;
}

static string BuildFrame(ColumnState[] columns, Random rng)
{
    var sb = new StringBuilder(Width * Height * 8);
    sb.Append("\u001b[2J\u001b[H");

    var grid = new char[Height, Width];
    var tone = new byte[Height, Width];

    for (var col = 0; col < Width; col++)
    {
        ref var state = ref columns[col];
        state.Y += state.Speed;
        if (state.Y - 14 > Height)
        {
            state.Y = -rng.Next(2, Height / 2);
            state.Speed = 1 + rng.Next(2);
            state.Char = MatrixCharPool[rng.Next(MatrixCharPool.Length)];
            state.Tick = rng.Next(4, 14);
        }

        if (--state.Tick <= 0)
        {
            state.Char = MatrixCharPool[rng.Next(MatrixCharPool.Length)];
            state.Tick = rng.Next(3, 12);
        }

        var head = state.Y;
        for (var trail = 0; trail < 14; trail++)
        {
            var row = head - trail;
            if (row < 0 || row >= Height)
                continue;

            grid[row, col] = trail == 0
                ? state.Char
                : MatrixCharPool[(col * 19 + row * 7 + trail * 3) % MatrixCharPool.Length];
            tone[row, col] = (byte)(trail == 0 ? 2 : 1);
        }
    }

    for (var row = 0; row < Height; row++)
    {
        for (var col = 0; col < Width; col++)
        {
            if (tone[row, col] == 0)
            {
                sb.Append(' ');
                continue;
            }

            if (tone[row, col] == 2)
                sb.Append("\u001b[97m"); // bright white head (palette 15; not matrix-tinted)
            else
                sb.Append("\u001b[32m");

            sb.Append(grid[row, col]);
        }

        sb.Append("\u001b[0m\n");
    }

    return sb.ToString();
}

static void WriteCast(string path, List<(double Time, string Data)> events)
{
    // Matrix-style: black bg, green trail (32m / palette 2), bright white heads (97m / palette 15).
    const string bg = "#000000";
    const string fg = "#00ff41";
    const string palette =
        "#000000:#3d0000:#008f11:#3d3d00:#0000cc:#3d003d:#00cccc:#888888:" +
        "#1a1a1a:#ff4444:#00ff41:#ffff44:#4444ff:#ff44ff:#44ffff:#ffffff";

    using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    writer.WriteLine(
        "{\"version\":3,\"term\":{\"cols\":80,\"rows\":24,\"type\":\"xterm-256color\"," +
        "\"theme\":{\"fg\":" + JsonString(fg) + ",\"bg\":" + JsonString(bg) + ",\"palette\":" + JsonString(palette) + "}}," +
        "\"timestamp\":1711000000,\"title\":\"Matrix Rain Tint\"," +
        "\"env\":{\"SHELL\":\"bash\"}," +
        "\"tags\":[\"st:font-size=16\",\"st:window=macos\"]}");

    var intervalError = 0.0;
    var previousAbs = 0.0;
    for (var i = 0; i < events.Count; i++)
    {
        var ev = events[i];
        var exact = i == 0 ? ev.Time : ev.Time - previousAbs;
        previousAbs = ev.Time;
        writer.Write('[');
        WriteCastInterval(writer, exact, ref intervalError);
        writer.Write(",\"o\",");
        WriteJsonString(writer, ev.Data);
        writer.WriteLine(']');
    }

    writer.Write('[');
    WriteCastInterval(writer, FrameSeconds, ref intervalError);
    writer.WriteLine(",\"x\",\"0\"]");
}

static void WriteCastInterval(TextWriter writer, double exact, ref double error)
{
    const double scale = 1000.0;
    var scaled = exact * scale + error;
    var quantized = Math.Round(scaled, MidpointRounding.AwayFromZero);
    error = scaled - quantized;
    var seconds = quantized / scale;
    if (seconds == 0)
    {
        writer.Write("0.000");
        return;
    }

    Span<char> buf = stackalloc char[16];
    seconds.TryFormat(buf, out var n, "0.000", CultureInfo.InvariantCulture);
    writer.Write(buf[..n]);
}

static string JsonString(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

static void WriteJsonString(TextWriter writer, string s)
{
    writer.Write('"');
    foreach (var c in s)
    {
        switch (c)
        {
            case '"': writer.Write("\\\""); break;
            case '\\': writer.Write("\\\\"); break;
            case '\b': writer.Write("\\b"); break;
            case '\f': writer.Write("\\f"); break;
            case '\n': writer.Write("\\n"); break;
            case '\r': writer.Write("\\r"); break;
            case '\t': writer.Write("\\t"); break;
            default:
                if (c < 0x20)
                    writer.Write("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else
                    writer.Write(c);
                break;
        }
    }

    writer.Write('"');
}

static string FindRepoRoot(string startDir)
{
    var dir = Path.GetFullPath(startDir);
    while (true)
    {
        if (File.Exists(Path.Combine(dir, "scenetake.cs")))
            return dir;

        var parent = Directory.GetParent(dir);
        if (parent is null)
            break;

        dir = parent.FullName;
    }

    Console.Error.WriteLine("Error: could not find scenetake.cs; run from the repository root");
    Environment.Exit(1);
    return "";
}

file struct ColumnState
{
    internal int Y;
    internal int Speed;
    internal char Char;
    internal int Tick;
}
