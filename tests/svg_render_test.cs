#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:include ../Terminal.cs
#:include ../CastReader.cs
#:include SvgTestStubs.cs
#:include ../Svg.cs

var failures = 0;
failures += Run("CurlOutputSurvivesFrameSampling", CurlOutputSurvivesFrameSampling);

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

static bool CurlOutputSurvivesFrameSampling()
{
    var recording = CastReader.Read("samples/basic.cast");
    var theme = TerminalTheme.FromResolved(recording.RenderSettings.Theme);
    var (cw, ch) = TerminalReplay.ResolveCanvasSize(recording.Width, recording.Height, recording.Events);
    var frames = TerminalReplay.BuildFrames(recording.Events, recording.Width, recording.Height, cw, ch, theme);
    var render = recording.RenderSettings with { MaxFps = 12 };
    var svg = SvgFrameRenderer.Render(frames, render, cw, ch);

    return svg.Contains("% Total", StringComparison.Ordinal)
        && svg.Contains("Could not resolve host: google.com", StringComparison.Ordinal)
        && svg.Contains("Dload  Upload", StringComparison.Ordinal);
}
