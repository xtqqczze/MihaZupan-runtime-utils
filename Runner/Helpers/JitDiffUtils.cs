﻿using System.IO.Pipelines;

namespace Runner.Helpers;

internal static partial class JitDiffUtils
{
    public static async Task RunJitDiffOnFrameworksAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder)
    {
        await RunJitDiffAsync(job, coreRootFolder, checkedClrFolder, outputFolder, "--frameworks");
    }

    public static async Task RunJitDiffOnAssemblyAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder, string assemblyPath)
    {
        await RunJitDiffAsync(job, coreRootFolder, checkedClrFolder, outputFolder, $"--assembly {assemblyPath}");
    }

    private static async Task RunJitDiffAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder, string frameworksOrAssembly)
    {
        bool useCctors = !job.TryGetFlag("nocctors");
        bool useTier0 = job.TryGetFlag("tier0");

        await job.LogAsync($"Using cctors for {coreRootFolder}: {useCctors}");
        await job.LogAsync($"Using tier0 {coreRootFolder}: {useTier0}");

        await job.RunProcessAsync("jitutils/bin/jit-diff",
            $"diff " +
            (useCctors ? "--cctors " : "") +
            (useTier0 ? "--tier0 " : "") +
            $"--output {outputFolder} " +
            $"{frameworksOrAssembly} --pmi " +
            $"--core_root {coreRootFolder} " +
            $"--base {checkedClrFolder}",
            logPrefix: $"jit-diff {coreRootFolder}");
    }

    public static async Task<string> RunJitAnalyzeAsync(JobBase job, string mainDirectory, string prDirectory, int count = 100)
    {
        List<string> output = [];

        await job.RunProcessAsync("jitutils/bin/jit-analyze",
            $"-b {mainDirectory} -d {prDirectory} -r -c {count}",
            output,
            logPrefix: "jit-analyze",
            checkExitCode: false);

        return string.Join('\n', output);
    }

    public static (string Description, string DasmFile, string Name)[] ParseDiffAnalyzeEntries(string diffSource, bool regressions)
    {
        ReadOnlySpan<char> text = diffSource.ReplaceLineEndings("\n");

        string start = regressions ? "Top method regressions" : "Top method improvements";
        int index = text.IndexOf(start, StringComparison.Ordinal);

        if (index < 0)
        {
            return Array.Empty<(string, string, string)>();
        }

        text = text.Slice(index);
        text = text.Slice(text.IndexOf('\n') + 1);
        text = text.Slice(0, text.IndexOf("\n\n", StringComparison.Ordinal));

        return text
            .ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JitDiffRegressionNameRegex().Match(line))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .ToArray();
    }

    public static string GetCommentMarkdown(string[] diffs, int lengthLimit, bool regressions, out bool lengthLimitExceeded)
    {
        lengthLimitExceeded = false;

        if (diffs.Length == 0)
        {
            return string.Empty;
        }

        int currentLength = 0;
        bool someChangesSkipped = false;

        List<string> changesToShow = new();

        foreach (var change in diffs)
        {
            if (change.Length > lengthLimit)
            {
                someChangesSkipped = true;
                lengthLimitExceeded = true;
                continue;
            }

            if ((currentLength += change.Length) > lengthLimit)
            {
                lengthLimitExceeded = true;
                break;
            }

            changesToShow.Add(change);
        }

        StringBuilder sb = new();

        sb.AppendLine($"## Top method {(regressions ? "regressions" : "improvements")}");
        sb.AppendLine();

        foreach (string md in changesToShow)
        {
            sb.AppendLine(md);
        }

        sb.AppendLine();

        if (someChangesSkipped)
        {
            sb.AppendLine("Note: some changes were skipped as they were too large to fit into a comment.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static async Task<(string[] Diffs, bool NoisyDiffsRemoved)> GetDiffMarkdownAsync(
        JobBase job,
        (string Description, string DasmFile, string Name)[] diffs,
        Func<string, string?>? tryGetExtraInfo,
        int maxCount)
    {
        if (diffs.Length == 0)
        {
            return (Array.Empty<string>(), false);
        }

        const string MainDasmDirectory = $"{JitDiffJob.DiffsMainDirectory}/{JitDiffJob.DasmSubdirectory}";
        const string PrDasmDirectory = $"{JitDiffJob.DiffsPrDirectory}/{JitDiffJob.DasmSubdirectory}";

        bool noisyMethodsRemoved = false;
        bool includeKnownNoise = job.TryGetFlag("includeKnownNoise");
        bool includeRemovedMethod = job.TryGetFlag("includeRemovedMethodImprovements");
        bool IncludeNewMethod = job.TryGetFlag("includeNewMethodRegressions");

        var result = await diffs
            .ToAsyncEnumerable()
            .Where(diff => includeRemovedMethod || !IsRemovedMethod(diff.Description))
            .Where(diff => IncludeNewMethod || !IsNewMethod(diff.Description))
            .SelectAwait(async diff =>
            {
                string mainDiffsFile = $"{MainDasmDirectory}/{diff.DasmFile}";
                string prDiffsFile = $"{PrDasmDirectory}/{diff.DasmFile}";

                await job.LogAsync($"Generating diffs for {diff.Name}");

                StringBuilder sb = new();

                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>{diff.Description} - {diff.Name}</summary>");
                sb.AppendLine();

                if (tryGetExtraInfo?.Invoke(diff.Name) is { } extraInfo)
                {
                    sb.AppendLine(extraInfo);
                    sb.AppendLine();
                }

                sb.AppendLine("```diff");

                using var baseFile = new TempFile("txt");
                using var prFile = new TempFile("txt");

                await File.WriteAllTextAsync(baseFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(mainDiffsFile, diff.Name));
                await File.WriteAllTextAsync(prFile.Path, await JitDiffUtils.TryGetMethodDumpAsync(prDiffsFile, diff.Name));

                List<string> lines = await GitHelper.DiffAsync(job, baseFile.Path, prFile.Path, fullContext: true);

                if (lines.Count == 0)
                {
                    return string.Empty;
                }
                else
                {
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("; ============================================================", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!includeKnownNoise && LineIsIndicativeOfKnownNoise(line.AsSpan().TrimStart()))
                        {
                            noisyMethodsRemoved = true;
                            return string.Empty;
                        }

                        sb.AppendLine(line);
                    }
                }

                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();

                return sb.ToString();
            })
            .Where(diff => !string.IsNullOrEmpty(diff))
            .Take(maxCount)
            .ToArrayAsync();

        return (result, noisyMethodsRemoved);

        static bool IsRemovedMethod(ReadOnlySpan<char> description) =>
            description.Contains("-100.", StringComparison.Ordinal);

        static bool IsNewMethod(ReadOnlySpan<char> description) =>
            description.Contains("∞ of base", StringComparison.Ordinal) ||
            description.Contains("Infinity of base", StringComparison.Ordinal);

        static bool LineIsIndicativeOfKnownNoise(ReadOnlySpan<char> line)
        {
            if (line.IsEmpty || line[0] is not ('+' or '-'))
            {
                return false;
            }

            return
                line.Contains("CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS", StringComparison.Ordinal) ||
                line.Contains("ProcessorIdCache:RefreshCurrentProcessorId", StringComparison.Ordinal) ||
                line.Contains("Interop+Sys:SchedGetCpu()", StringComparison.Ordinal);
        }
    }

    public static async Task<string> TryGetMethodDumpAsync(string diffPath, string methodName)
    {
        using var fs = File.Open(diffPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var pipe = PipeReader.Create(fs);

        bool foundPrefix = false;
        bool foundSuffix = false;
        byte[] prefix = Encoding.ASCII.GetBytes($"; Assembly listing for method {methodName}");
        byte[] suffix = Encoding.ASCII.GetBytes("; ============================================================");

        StringBuilder sb = new();

        while (true)
        {
            ReadResult result = await pipe.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition? position = null;

            do
            {
                position = buffer.PositionOf((byte)'\n');

                if (position != null)
                {
                    var line = buffer.Slice(0, position.Value);

                    ProcessLine(
                        line.IsSingleSegment ? line.FirstSpan : line.ToArray(),
                        prefix, suffix, ref foundPrefix, ref foundSuffix);

                    if (foundPrefix)
                    {
                        sb.AppendLine(Encoding.UTF8.GetString(line));

                        if (sb.Length > 1024 * 1024)
                        {
                            return string.Empty;
                        }
                    }

                    if (foundSuffix)
                    {
                        return sb.ToString();
                    }

                    buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                }
            }
            while (position != null);

            pipe.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                return string.Empty;
            }
        }

        static void ProcessLine(ReadOnlySpan<byte> line, byte[] prefix, byte[] suffix, ref bool foundPrefix, ref bool foundSuffix)
        {
            if (foundPrefix)
            {
                if (line.StartsWith(suffix))
                {
                    foundSuffix = true;
                }
            }
            else
            {
                if (line.StartsWith(prefix))
                {
                    foundPrefix = true;
                }
            }
        }
    }

    [GeneratedRegex(@" *(.*?) : (.*?) - ([^ ]*)")]
    private static partial Regex JitDiffRegressionNameRegex();
}