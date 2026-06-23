using System.Diagnostics;

internal readonly record struct PipeStreamChunk(TimeSpan Time, bool IsStderr, string Text);

internal static class PipeStreamCapture
{
    internal static async Task<IReadOnlyList<PipeStreamChunk>> ReadAsync(Process process, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var stdoutChunks = new List<PipeStreamChunk>();
        var stderrChunks = new List<PipeStreamChunk>();

        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdoutChunks, isStderr: false, stopwatch, cancellationToken);
        var stderrTask = ReadStreamAsync(process.StandardError, stderrChunks, isStderr: true, stopwatch, cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);

        return MergeChronologically(stdoutChunks, stderrChunks);
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        List<PipeStreamChunk> chunks,
        bool isStderr,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            chunks.Add(new PipeStreamChunk(stopwatch.Elapsed, isStderr, new string(buffer, 0, read)));
        }
    }

    private static List<PipeStreamChunk> MergeChronologically(
        IReadOnlyList<PipeStreamChunk> stdoutChunks,
        IReadOnlyList<PipeStreamChunk> stderrChunks)
    {
        if (stdoutChunks.Count == 0)
            return [.. stderrChunks];
        if (stderrChunks.Count == 0)
            return [.. stdoutChunks];

        var merged = new List<PipeStreamChunk>(stdoutChunks.Count + stderrChunks.Count);
        merged.AddRange(stdoutChunks);
        merged.AddRange(stderrChunks);
        merged.Sort(static (left, right) => left.Time.CompareTo(right.Time));
        return merged;
    }
}
