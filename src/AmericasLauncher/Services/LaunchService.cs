// SPDX-License-Identifier: GPL-2.0-only
using System.Diagnostics;
using System.IO;
using AmericasLauncher.Models;

namespace AmericasLauncher.Services;

public sealed class LaunchService
{
    /// <summary>
    /// Runs the optional pre-launch commands (pipeline / shader translation) and then starts the game.
    /// Cancelling <paramref name="ct"/> stops a running pre-launch command (and its child processes)
    /// and throws <see cref="OperationCanceledException"/>.
    /// <paramref name="onExited"/> is called (possibly from a background thread) when the game process exits.
    /// </summary>
    public async Task<Process> Launch(
        RunnerProfile p, string gameFolder, string gameExe, Action<string> log,
        bool usePipeline = true, Action? onExited = null, CancellationToken ct = default,
        RunnerLaunchOptions? options = null, bool seriesS = false, string? resolution = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var exe = RunnerService.ExePath(p);
        if (!File.Exists(exe)) throw new FileNotFoundException($"{p.Name} is not installed yet.", exe);
        if (!Directory.Exists(gameFolder)) throw new DirectoryNotFoundException("Game folder not found: " + gameFolder);
        if (!File.Exists(gameExe)) throw new FileNotFoundException("Game exe not found.", gameExe);

        var workDir = Path.GetDirectoryName(exe)!;

        // Windows 10: the runner needs the D3D12 Agility SDK (AgilityService).
        if (AgilityService.Needed)
        {
            if (AgilityService.HasExports(exe))
                await AgilityService.EnsureRuntimeAsync(exe, log, ct);
            else
                log("WARNING: Windows 10 and this runner was built without the D3D12 Agility SDK: it will likely fail " +
                    "with \"the device supports SM 0x65 only\". Press Install / Update (or Build) to build it again.");
        }

        // The user's launch settings for this runner (Settings > Launch Options), for every process below.
        var env = LaunchOptionsService.ResolveEnv(options, RunnerService.SourceDir(p), gameFolder, gameExe, log);
        if (!env.ContainsKey("VICTORY_NO_WATCHDOG"))
            env["VICTORY_NO_WATCHDOG"] = "1";
        if (string.Equals(p.Id, "klbf-lab", StringComparison.OrdinalIgnoreCase))
        {
            if (!env.ContainsKey("VICTORY_SHADER_OPT")) env["VICTORY_SHADER_OPT"] = "0";
            if (!env.ContainsKey("VICTORY_LATE_WAIT")) env["VICTORY_LATE_WAIT"] = "1";
            if (!env.ContainsKey("VICTORY_WIDE_BACK")) env["VICTORY_WIDE_BACK"] = "1";
            if (!env.ContainsKey("VICTORY_PIPELINE_WAIT_MS")) env["VICTORY_PIPELINE_WAIT_MS"] = "24";
            if (!env.ContainsKey("VICTORY_RELAY_COPY_QUEUE")) env["VICTORY_RELAY_COPY_QUEUE"] = "0";
        }
        if (!string.IsNullOrWhiteSpace(RunnerService.CustomCacheRoot) && !env.ContainsKey("VICTORY_CACHE_DIR"))
            env["VICTORY_CACHE_DIR"] = RunnerService.CustomCacheRoot;
        var resValue = resolution switch
        {
            "540p" => "960x540",
            "720p" => "1280x720",
            "1080p" => "1920x1080",
            "2K" => "2560x1440",
            "4K" => "3840x2160",
            _ => null
        };
        if (resValue != null && !env.ContainsKey("VICTORY_RESOLUTION"))
            env["VICTORY_RESOLUTION"] = resValue;
        if (options is { IsEmpty: false })
        {
            log($"Launch options for {p.Name}: {env.Count} environment variable(s)" +
                (string.IsNullOrWhiteSpace(options.ExtraArgs) ? "" : $", extra arguments: {options.ExtraArgs.Trim()}"));
            if (env.Count > 0) log("  " + string.Join(", ", env.Keys));
        }

        // 1) Run the pre-launch commands (e.g. translate) one by one and wait for each to finish.
        if (usePipeline && p.PreLaunchArgs.Count > 0)
        {
            log("WARNING: Pipeline/shader translation is enabled. This will take a long time (several minutes on the first run) — please wait...");

            // Some runners print nothing while they translate. If the log stays silent for a while,
            // the heartbeat below shows that it is still working (elapsed time, victory.exe CPU, cache size).
            var lastOutputTicks = DateTime.UtcNow.Ticks;
            void TrackedLog(string line)
            {
                Interlocked.Exchange(ref lastOutputTicks, DateTime.UtcNow.Ticks);
                log(line);
            }

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatAsync(
                p, log, () => new DateTime(Interlocked.Read(ref lastOutputTicks), DateTimeKind.Utc),
                DateTime.UtcNow, heartbeatCts.Token);
            try
            {
                foreach (var rawArgs in p.PreLaunchArgs)
                {
                    ct.ThrowIfCancellationRequested();
                    var preArgs = FormatArgs(rawArgs, gameFolder, gameExe);
                    log($"> {preArgs}");
                    var code = await ProcessRunner.RunAsync(exe, preArgs, workDir, TrackedLog, ct, env);
                    if (code != 0)
                        throw new InvalidOperationException($"Pre-launch command failed with exit code {code}.");
                }
            }
            finally
            {
                heartbeatCts.Cancel();
                await heartbeat;   // never throws, see HeartbeatAsync
            }
        }

        // 2) Start the game itself.
        // The game's output goes straight into a log file (cmd redirects it), NOT through a pipe owned by the launcher.
        // With a pipe, a slow reader (the launcher's UI) fills the small pipe buffer and the game's logging threads
        // block inside victory.exe. A file never blocks the game, and the game also survives the launcher closing.
        ct.ThrowIfCancellationRequested();
        var args = FormatArgs(LaunchOptionsService.ComposeArgs(p.LaunchArgs, options), gameFolder, gameExe);
        var envConsole = p.UsesConsoleEnv ? (seriesS ? "lockhart" : "anaconda") : null;
        if (p.SupportsConsole && !p.UsesConsoleEnv)
            args += seriesS ? " --console series-s" : " --console series-x";
        if (p.SupportsConsole)
            log($"Console: {(seriesS ? "Xbox Series S" : "Xbox Series X")}");
        log($"> {args}");

        Directory.CreateDirectory(Path.GetDirectoryName(GameLogPath)!);
        try { File.WriteAllText(GameLogPath, ""); } catch { /* the old log is still locked: append to it */ }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/d /c \"\"%AL_EXE%\" %AL_ARGS% >>\"%AL_LOG%\" 2>&1\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        LaunchOptionsService.Apply(psi.Environment, env);
        // The runner's benchmark variables (Benchmark button), over the user's launch options.
        if (extraEnv != null)
            foreach (var (key, value) in extraEnv)
            {
                psi.Environment[key] = value;
                log($"Benchmark: {key}={value}");
            }
        psi.Environment["AL_EXE"] = exe;
        psi.Environment["AL_ARGS"] = args;
        psi.Environment["AL_LOG"] = GameLogPath;
        if (envConsole != null) psi.Environment["XCL_DEVICE_TYPE"] = envConsole;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tailDone = new CancellationTokenSource();
        Task? tail = null;
        process.Exited += (_, _) =>
        {
            try
            {
                tailDone.Cancel();
                tail?.Wait(2000);
                log($"Process exited with code {process.ExitCode}.");
            }
            catch { /* ignore if window already closed */ }

            try { onExited?.Invoke(); }
            catch { /* ignore if window already closed */ }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the process.");
        }

        tail = Task.Run(() => TailAsync(GameLogPath, log, tailDone.Token));
        return process;
    }

    public static string GameLogPath => Path.Combine(Paths.Root, "logs", "game.log");

    /// <summary>Follows the game's log file and passes new lines to <paramref name="log"/>. Drains what is left when cancelled.</summary>
    private static async Task TailAsync(string path, Action<string> log, CancellationToken stop)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line != null) { log(line); continue; }
                if (stop.IsCancellationRequested) break;
                try { await Task.Delay(250, stop); } catch (OperationCanceledException) { }
            }
        }
        catch { /* the log is only a convenience; never affect the game */ }
    }

    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// While the runner is silent for <see cref="HeartbeatEvery"/>, writes a status line with the elapsed time,
    /// the runner exe's CPU usage (percent of the whole CPU, like Task Manager) and the pipeline cache size.
    /// Runners that print their own progress (e.g. VIctory Original) keep it quiet because they are never silent that long.
    /// Never throws; it ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    private static async Task HeartbeatAsync(
        RunnerProfile p, Action<string> log, Func<DateTime> lastOutput, DateTime started, CancellationToken ct)
    {
        try
        {
            var name = Path.GetFileName(p.Exe);
            var cores = Environment.ProcessorCount;
            var lastBeat = DateTime.UtcNow;
            var (prevCpu, _) = RunnerService.GetRunnerCpuTime(p);
            var prevAt = lastBeat;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                var now = DateTime.UtcNow;
                var quietSince = lastOutput() > lastBeat ? lastOutput() : lastBeat;
                if (now - quietSince < HeartbeatEvery) continue;

                var (cpu, count) = RunnerService.GetRunnerCpuTime(p);
                var wall = (now - prevAt).TotalSeconds;
                var cpuText = "n/a";
                if (count > 0 && wall > 0)
                {
                    var used = Math.Max(0, (cpu - prevCpu).TotalSeconds);
                    cpuText = $"{Math.Clamp(used / (wall * cores) * 100, 0, 100):0}%";
                }
                prevCpu = cpu;
                prevAt = now;

                var cacheDir = RunnerService.PipelineCacheDir;
                var cacheText = await Task.Run(
                    () => Directory.Exists(cacheDir)
                        ? ", cache size: " + RunnerService.FormatBytes(RunnerService.GetDirectorySize(cacheDir))
                        : "",
                    ct);

                var elapsed = now - started;
                var time = elapsed.ToString(elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
                log($"Still working... {time} elapsed, {name} CPU: {cpuText}{cacheText}");
                lastBeat = now;
            }
        }
        catch
        {
            // Cancelled, or the window is closing: the heartbeat is only a convenience.
        }
    }

    private static string FormatArgs(string template, string gameFolder, string gameExe) =>
        template
            .Replace("{gameFolder}", gameFolder.TrimEnd('\\', '/'))
            .Replace("{gameExe}", gameExe);
}
