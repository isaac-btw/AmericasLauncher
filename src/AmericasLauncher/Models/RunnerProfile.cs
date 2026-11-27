// SPDX-License-Identifier: GPL-2.0-only
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace AmericasLauncher.Models;

public enum RunnerHealthState
{
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    Broken
}

public sealed class Dependency
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>winget package id, e.g. "Git.Git".</summary>
    [JsonPropertyName("wingetId")] public string WingetId { get; set; } = "";

    /// <summary>"command:&lt;exe&gt;" (found via PATH) or "vsbuildtools".</summary>
    [JsonPropertyName("check")] public string Check { get; set; } = "";

    /// <summary>Optional installer arguments passed through winget's --override.</summary>
    [JsonPropertyName("wingetOverride")] public string? WingetOverride { get; set; }
}

public sealed class RunnerProfile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private RunnerHealthState _healthState = RunnerHealthState.NotInstalled;

    [JsonIgnore]
    public RunnerHealthState HealthState
    {
        get => _healthState;
        set
        {
            if (_healthState == value) return;
            _healthState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HealthState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColor)));
        }
    }

    [JsonIgnore]
    public string StatusColor => HealthState switch
    {
        RunnerHealthState.UpToDate => "#22C55E",        // Green
        RunnerHealthState.Broken => "#EF4444",          // Red
        RunnerHealthState.UpdateAvailable => "#EAB308", // Yellow
        _ => "#0F172A"                                  // Black (Not Installed)
    };
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("gitUrl")] public string GitUrl { get; set; } = "";
    [JsonPropertyName("dependencies")] public List<Dependency> Dependencies { get; set; } = new();

    /// <summary>Commands run (via cmd.exe) inside the cloned source folder.</summary>
    [JsonPropertyName("buildCommands")] public List<string> BuildCommands { get; set; } = new();

    /// <summary>Path of the built executable, relative to the source folder.</summary>
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";

    [JsonPropertyName("preLaunchArgs")] public List<string> PreLaunchArgs { get; set; } = new();
   
    /// <summary>Arguments for the built executable. Supports {gameFolder} and {gameExe}.</summary>
    [JsonPropertyName("launchArgs")] public string LaunchArgs { get; set; } = "";

    /// <summary>True when the runner can choose between Xbox Series S and Series X (all VIctory forks and XCL).</summary>
    [JsonIgnore] public bool SupportsConsole => UsesConsoleEnv || LaunchArgs.Contains("--execute");

    /// <summary>XCL picks the console from the XCL_DEVICE_TYPE environment variable; VIctory forks use --console.</summary>
    [JsonIgnore] public bool UsesConsoleEnv => string.Equals(Id, "xcl", StringComparison.OrdinalIgnoreCase);

    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>
    /// Optional: environment variables that turn on the runner's own built-in benchmark. When present, a
    /// "Benchmark" button launches the game like "Launch Game" with these added (over the user's own launch options).
    /// </summary>
    [JsonPropertyName("benchmarkEnv")] public Dictionary<string, string>? BenchmarkEnv { get; set; }
    [JsonIgnore] public bool HasBenchmark => BenchmarkEnv is { Count: > 0 };

    public override string ToString() => Name;
}
