﻿namespace Runner.Helpers;

internal static class RuntimeHelpers
{
    private static void AssertIsLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }
    }

    public static string LibrariesExtraBuildArgs => OperatingSystem.IsLinux()
        ? "-p:RunAnalyzers=false -p:ApiCompatValidateAssemblies=false"
        : "/p:RunAnalyzers=false /p:ApiCompatValidateAssemblies=false";

    public static async Task CloneRuntimeMainAsync(JobBase job)
    {
        const string LogPrefix = "Setup runtime";

        if (OperatingSystem.IsLinux())
        {
            string script =
                $$$"""
                set -e

                git clone --no-tags --branch main --single-branch --progress https://github.com/dotnet/runtime runtime
                cd runtime

                git log -1
                chmod 777 build.sh
                git config --global user.email build@build.foo
                git config --global user.name build

                eng/common/native/install-dependencies.sh linux
                """;

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await job.RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);
        }
        else
        {
            string script =
                $$$"""
                git config --system core.longpaths true
                git clone --no-tags --branch main --single-branch --progress https://github.com/dotnet/runtime runtime
                cd runtime

                git log -1
                git config --global user.email build@build.foo
                git config --global user.name build
                """;

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("clone-runtime.bat", script);
            await job.RunProcessAsync("clone-runtime.bat", string.Empty, logPrefix: LogPrefix);
        }
    }

    public static async Task CloneRuntimeAsync(JobBase job)
    {
        const string LogPrefix = "Setup runtime";

        if (OperatingSystem.IsLinux())
        {
            string script = UpdateMergePlaceholders(
                $$$"""
                set -e

                git clone --no-tags --branch {{{job.BaseBranch}}} --single-branch --progress https://github.com/{{{job.BaseRepo}}} runtime
                cd runtime

                git log -1
                chmod 777 build.sh
                git config --global user.email build@build.foo
                git config --global user.name build

                {{MERGE_BASELINE_BRANCHES}}

                git switch -c pr

                {{MERGE_PR_BRANCHES}}

                git switch {{{job.BaseBranch}}}

                eng/common/native/install-dependencies.sh linux
                """);

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await job.RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);
        }
        else
        {
            string script = UpdateMergePlaceholders(
                $$$"""
                git config --system core.longpaths true
                git clone --no-tags --branch {{{job.BaseBranch}}} --single-branch --progress https://github.com/{{{job.BaseRepo}}} runtime
                cd runtime

                git log -1
                git config --global user.email build@build.foo
                git config --global user.name build

                {{MERGE_BASELINE_BRANCHES}}

                git switch -c pr

                {{MERGE_PR_BRANCHES}}

                git switch {{{job.BaseBranch}}}
                """);

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("clone-runtime.bat", script);
            await job.RunProcessAsync("clone-runtime.bat", string.Empty, logPrefix: LogPrefix);
        }

        string UpdateMergePlaceholders(string template)
        {
            return template
                .ReplaceLineEndings()
                .Replace("{{MERGE_BASELINE_BRANCHES}}", GetMergeScript("dependsOn"), StringComparison.Ordinal)
                .Replace("{{MERGE_PR_BRANCHES}}", GetMergeScript("combineWith"), StringComparison.Ordinal);
        }

        string GetMergeScript(string name)
        {
            int counter = 0;

            List<(string Repo, string Branch)> prList = new(GetPRList(job, name));

            if (name == "combineWith")
            {
                prList.Insert(0, (job.PrRepo, job.PrBranch));
            }

            return string.Join('\n', prList
                .Select(pr =>
                {
                    int index = ++counter;
                    string remoteName = $"{name}{index}";

                    return
                        $"git remote add {remoteName} https://github.com/{pr.Repo}\n" +
                        $"git fetch {remoteName} {pr.Branch}\n" +
                        $"git log {remoteName}/{pr.Branch} -1\n" +
                        $"git merge --no-edit {remoteName}/{pr.Branch}\n" +
                        $"git log -1\n";
                }));
        };

        static (string Repo, string Branch)[] GetPRList(JobBase job, string name)
        {
            if (job.Metadata.TryGetValue(name, out string? value))
            {
                return value.Split(',').Select(pr =>
                {
                    string[] parts = pr.Split(';');
                    return (parts[0], parts[1]);
                }).ToArray();
            }

            return [];
        }
    }

    public static async Task InstallRuntimeDotnetSdkAsync(JobBase job)
    {
        await InstallDotnetSdkAsync(job, "runtime/global.json");
    }

    public static async Task InstallDotnetSdkAsync(JobBase job, string globalJsonPath)
    {
        AssertIsLinux();

        await job.RunProcessAsync("wget", "https://dot.net/v1/dotnet-install.sh");
        await job.RunProcessAsync("bash", $"dotnet-install.sh --jsonfile {globalJsonPath} --install-dir /usr/lib/dotnet");
    }

    public static async Task CopyReleaseArtifactsAsync(JobBase job, string logPrefix, string destination, string runtimeConfig = "Release")
    {
        AssertIsLinux();

        await job.RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{JobBase.Arch}.{runtimeConfig}/. {destination}", logPrefix: logPrefix);

        const string BaseDirectory = "runtime/artifacts/bin/runtime";

        string folder = Directory.GetDirectories(BaseDirectory)
            .Select(f => Path.GetRelativePath(BaseDirectory, f))
            .Where(f => f.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains("linux", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains(JobBase.Arch, StringComparison.OrdinalIgnoreCase))
            .Single();

        await job.RunProcessAsync("cp", $"-r {BaseDirectory}/{folder}/. {destination}", logPrefix: logPrefix);
    }

    public static int GetDotnetVersion(string repository = "runtime")
    {
        // "version": "10.0.100-preview.1.12345.6", => 10
        return int.Parse(File.ReadAllLines($"{repository}/global.json")
            .First(line => line.Contains("version", StringComparison.OrdinalIgnoreCase))
            .Split(':')[1] //  "10.0.100-preview.1.12345.6"
            .Split('.')[0] //  "10
            .TrimStart(' ', '"'));
    }
}
