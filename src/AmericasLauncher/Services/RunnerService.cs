// SPDX-License-Identifier: GPL-2.0-only
using System.Diagnostics;
using System.IO;
using AmericasLauncher.Models;

namespace AmericasLauncher.Services;

public enum UpdateStatus { NotInstalled, UpToDate, UpdateAvailable, Unknown }

/// <summary>Installs requirements, downloads the runner's source with git and builds it.</summary>
public sealed class RunnerService
{
    private readonly DependencyService _dependencies = new();

    public static string SourceDir(RunnerProfile p) => Path.Combine(Paths.Root, "sources", p.Id);
    public static string ExePath(RunnerProfile p) => Path.Combine(SourceDir(p), p.Exe);
    public static string PipelineCacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "victory",
        "cache");

    public static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* ignore locked/inaccessible files */ }
            }
        }
        catch { /* ignore */ }
        return total;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var idx = 0;
        while (size >= 1024 && idx < units.Length - 1)
        {
            size /= 1024;
            idx++;
        }
        return $"{size:0.##} {units[idx]}";
    }

    public static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    public bool IsInstalled(RunnerProfile p) => File.Exists(ExePath(p));

    /// <summary>
    /// Stops every running process that was started from this runner's own built exe
    /// (e.g. a leftover victory.exe in the background). Processes with the same name that live
    /// somewhere else, such as another runner's victory.exe, are not touched.
    /// Returns how many processes were stopped.
    /// </summary>
    public static int KillRunnerProcesses(RunnerProfile p)
    {
        var exePath = Path.GetFullPath(ExePath(p));
        var killed = 0;

        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            try
            {
                var path = proc.MainModule?.FileName;
                if (path != null && string.Equals(Path.GetFullPath(path), exePath, StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch { /* access denied, or the process already exited */ }
            finally { proc.Dispose(); }
        }

        return killed;
    }

    /// <summary>Short git revision of the installed source, or "" if unknown.</summary>
    public async Task<string> GetInstalledVersionAsync(RunnerProfile p, CancellationToken ct = default)
    {
        var dir = SourceDir(p);
        if (!Directory.Exists(Path.Combine(dir, ".git"))) return "";
        try { return await GetHeadAsync(dir, ct); }
        catch { return ""; }
    }

    /// <summary>Compares the installed revision with the remote default branch (needs internet).</summary>
    public async Task<UpdateStatus> CheckForUpdateAsync(RunnerProfile p, CancellationToken ct = default)
    {
        if (!IsInstalled(p)) return UpdateStatus.NotInstalled;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            var local = await GetHeadAsync(SourceDir(p), cts.Token, full: true);
            if (local == "") return UpdateStatus.Unknown;

            var lines = new List<string>();
            var code = await ProcessRunner.RunAsync(
                "git", $"ls-remote \"{p.GitUrl}\" HEAD", null,
                s => { lock (lines) lines.Add(s); }, cts.Token);
            if (code != 0) return UpdateStatus.Unknown;

            string remote;
            lock (lines)
            {
                remote = lines.Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length >= 40 && l[..40].All(Uri.IsHexDigit))?[..40] ?? "";
            }
            if (remote == "") return UpdateStatus.Unknown;

            return string.Equals(local, remote, StringComparison.OrdinalIgnoreCase)
                ? UpdateStatus.UpToDate
                : UpdateStatus.UpdateAvailable;
        }
        catch
        {
            return UpdateStatus.Unknown;   // offline, git missing, timeout ...
        }
    }

    /// <summary>
    /// The runner's card colour. With <paramref name="config"/>, a runner whose last build did not finish
    /// counts as needing an update even when its source is current.
    /// </summary>
    public async Task<RunnerHealthState> CheckHealthAsync(
        RunnerProfile p, AppConfig? config = null, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var lines = new List<string>();
            var code = await ProcessRunner.RunAsync(
                "git", $"ls-remote \"{p.GitUrl}\" HEAD", null,
                s => { lock (lines) lines.Add(s); }, cts.Token);

            if (code != 0)
                return RunnerHealthState.Broken;

            if (!IsInstalled(p))
                return RunnerHealthState.NotInstalled;

            var local = await GetHeadAsync(SourceDir(p), cts.Token, full: true);
            string remote;
            lock (lines)
            {
                remote = lines.Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length >= 40 && l[..40].All(Uri.IsHexDigit))?[..40] ?? "";
            }

            if (local != "" && remote != "" && !string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
                return RunnerHealthState.UpdateAvailable;

            if (config != null && await NeedsRebuildAsync(p, config, cts.Token))
                return RunnerHealthState.UpdateAvailable;

            return RunnerHealthState.UpToDate;
        }
        catch
        {
            return IsInstalled(p) ? RunnerHealthState.UpToDate : RunnerHealthState.NotInstalled;
        }
    }

    /// <summary>Returns true when the runner is built and ready to launch.</summary>
    public async Task<bool> InstallOrUpdateAsync(
        RunnerProfile p, AppConfig config, Action<string> log, CancellationToken ct = default)
    {
        // 1) Requirements (git, build tools, rust ...)
        log($"=== {p.Name}: checking requirements ===");
        foreach (var dependency in p.Dependencies)
        {
            if (!await _dependencies.EnsureAsync(dependency, log, ct)) return false;
        }

        // 2) Source code
        var dir = SourceDir(p);
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            log("=== Updating source ===");
            if (await ProcessRunner.RunAsync("git", "pull --ff-only", dir, log, ct) != 0)
            {
                log("[!!] git pull failed.");
                return false;
            }
        }
        else
        {
            log("=== Downloading source ===");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);   // leftover from a failed clone
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            if (await ProcessRunner.RunAsync("git", $"clone --depth 1 \"{p.GitUrl}\" \"{dir}\"", null, log, ct) != 0)
            {
                log("[!!] git clone failed.");
                return false;
            }
        }

        // Skip the build only when this exact revision was built successfully before. Comparing with the
        // revision before the pull is not enough: if a build failed after a pull, the source is already
        // current but the exe is still the old one, and the build would never be tried again.
        var after = await GetHeadAsync(dir, ct);
        if (IsInstalled(p) && IsBuiltRevision(p, config, after))
        {
            log($"Already up to date ({after}).");
            return true;
        }

        // 3) Build
        log("=== Building (this can take several minutes) ===");
        foreach (var command in p.BuildCommands)
        {
            log($"> {command}");
            var code = await ProcessRunner.RunAsync("cmd.exe", "/c " + command, dir, log, ct);
            if (code != 0)
            {
                log($"[!!] Command failed with exit code {code}.");
                return false;
            }
        }

        if (!IsInstalled(p))
        {
            log($"[!!] Build finished, but '{p.Exe}' was not found.");
            return false;
        }

        config.InstalledVersions[p.Id] = after;
        log($"[ok] {p.Name} is ready ({after}).");
        return true;
    }

    /// <summary>True when <paramref name="revision"/> is the revision recorded after the last successful build.</summary>
    private static bool IsBuiltRevision(RunnerProfile p, AppConfig config, string revision)
    {
        if (revision == "" || !config.InstalledVersions.TryGetValue(p.Id, out var built) || string.IsNullOrEmpty(built))
            return false;

        // Short hashes can differ in length (git lengthens them when needed), so compare by prefix.
        return revision.StartsWith(built, StringComparison.OrdinalIgnoreCase)
            || built.StartsWith(revision, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the runner is installed but its source is at a revision that was never built successfully,
    /// e.g. a build failed after the new source had already been pulled.
    /// </summary>
    public async Task<bool> NeedsRebuildAsync(RunnerProfile p, AppConfig config, CancellationToken ct = default)
    {
        if (!IsInstalled(p)) return false;
        var head = await GetInstalledVersionAsync(p, ct);
        return head != "" && !IsBuiltRevision(p, config, head);
    }

    private static async Task<string> GetHeadAsync(string dir, CancellationToken ct, bool full = false)
    {
        var lines = new List<string>();
        var code = await ProcessRunner.RunAsync(
            "git", full ? "rev-parse HEAD" : "rev-parse --short HEAD", dir, s => { lock (lines) lines.Add(s); }, ct);

        lock (lines) return code == 0 ? (lines.FirstOrDefault()?.Trim() ?? "") : "";
    }
}
