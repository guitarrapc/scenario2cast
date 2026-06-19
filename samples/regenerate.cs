#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable

// Regenerate sample .cast and .svg files from every *.yaml in this directory.
//
// Usage (from repository root):
//   dotnet run samples/regenerate.cs

using System.Diagnostics;

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
var toolPath = Path.Combine(repoRoot, "scenetake.cs");
var samplesDir = Path.Combine(repoRoot, "samples");

if (!File.Exists(toolPath))
{
    Console.Error.WriteLine($"Error: {toolPath} not found");
    return 1;
}

var scenarios = Directory
    .EnumerateFiles(samplesDir, "*.yaml", SearchOption.TopDirectoryOnly)
    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
    .ToArray();

if (scenarios.Length == 0)
{
    Console.Error.WriteLine($"Error: no .yaml files in {samplesDir}");
    return 1;
}

var failures = 0;
foreach (var scenario in scenarios)
{
    var name = Path.GetFileName(scenario);
    Console.Error.WriteLine();
    Console.Error.WriteLine($"=== {name} ===");

    if (RunScenario(repoRoot, toolPath, scenario) != 0)
        failures++;
}

Console.Error.WriteLine();
Console.Error.WriteLine("=== matrix-gen.cs ===");
if (RunScript(repoRoot, "samples/matrix-gen.cs") != 0)
    failures++;
else if (RunSvgCast(repoRoot, toolPath, "samples/matrix.cast") != 0)
    failures++;

var total = scenarios.Length + 1;
if (failures > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Failed: {failures}/{total} sample(s)");
    return 1;
}

Console.Error.WriteLine();
Console.Error.WriteLine($"Done: {total} sample(s)");
return 0;

static int RunScenario(string repoRoot, string toolPath, string scenarioPath)
{
    var relativeScenario = Path.GetRelativePath(repoRoot, scenarioPath).Replace('\\', '/');

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = repoRoot,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add(toolPath);
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add("--format");
    psi.ArgumentList.Add("svg");
    psi.ArgumentList.Add(relativeScenario);

    using var process = Process.Start(psi);
    if (process is null)
    {
        Console.Error.WriteLine("Error: failed to start dotnet");
        return 1;
    }

    process.WaitForExit();
    return process.ExitCode;
}

static int RunScript(string repoRoot, string scriptPath)
{
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = repoRoot,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add(scriptPath);

    using var process = Process.Start(psi);
    if (process is null)
    {
        Console.Error.WriteLine("Error: failed to start dotnet");
        return 1;
    }

    process.WaitForExit();
    return process.ExitCode;
}

static int RunSvgCast(string repoRoot, string toolPath, string castPath)
{
    var relativeCast = Path.GetRelativePath(repoRoot, castPath).Replace('\\', '/');
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = repoRoot,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add(toolPath);
    psi.ArgumentList.Add("--");
    psi.ArgumentList.Add("svg");
    psi.ArgumentList.Add(relativeCast);

    using var process = Process.Start(psi);
    if (process is null)
    {
        Console.Error.WriteLine("Error: failed to start dotnet");
        return 1;
    }

    process.WaitForExit();
    return process.ExitCode;
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
