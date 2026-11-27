**English** | [Türkçe](README.tr.md) | [Русский](README.ru.md) | [中文](README.zh.md) | [Español](README.es.md) | [Italiano](README.it.md) | [Polski](README.pl.md) | [Português (BR)](README.pt-BR.md) | [हिन्दी](README.hi.md)

# Americas Launcher

A modern Windows launcher that automatically sets up, updates, builds, and launches Xbox Series X|S (GDK) compatibility layers on Windows — with live port progress tracking built in.

> This project is intended **only for games you legally own**, unpacked on your own PC. Americas Launcher does **not** include, download, or distribute games, keys, or console SDK files..

![Americas Launcher - Launch View](docs/images/launch-view.png)

## Features

- **Modern 720p UI:** Clean dark navy & patriot-themed interface with custom window controls and 9 selectable runner cards.
- **One-Click Install / Update:**
  - Automatically checks and installs missing dependencies via `winget` (Git, Visual Studio C++ Build Tools, Rust).
  - Clones and updates each runner's source repository with `git`, then compiles it locally.
  - Displays the installed commit hash and checks upstream for updates.
- **Integrated Console Output:** Runner output and pre-launch shader/pipeline translation logs stream directly inside the launcher's log box — no separate CMD window pops up.
- **Pre-Launch Pipeline Support:** Runs `preLaunchArgs` (such as `translate --pipelines`) sequentially before starting the game, with a clear notice when first-time translation may take several minutes.
- **"Still working" status during the pipeline:** Some runners print nothing while they translate shaders. If the log stays silent for 15 seconds, the launcher writes a status line with the elapsed time, the runner exe's CPU usage (percent of the whole CPU, like Task Manager) and the size of the pipeline cache, for example `Still working... 02:30 elapsed, victory.exe CPU: 37%, cache size: 412 MB`. It cannot show a percentage, because only the runner knows how much work is left; runners that print their own progress (such as VIctory Original) are not interrupted.
- **Stop Game at any time:** The Launch button turns into **Stop Game** as soon as you click it — also while the pipeline translation is still running. It cancels the translation, closes the game and any leftover runner process (such as `victory.exe`) still running in the background.
- **Xbox Series X / S selector:** A button next to the pipeline toggle chooses the console per runner (VIctory forks: `--console series-x|series-s`; XCL: `XCL_DEVICE_TYPE`). Shows "N/A" for runners without the option.
- **Game output in a log file:** The game's output is written to `%LocalAppData%\AmericasLauncher\logs\game.log` and shown in the log box in batches, so the launcher can never block the game.
- **Update notice:** On startup the launcher checks for updates and shows "- New Update available" by the title and a yellow dot next to Settings.
- **Settings & Self-Update Check:** Configure your Game Folder and Game Exe paths, and check GitHub Releases for newer versions of Americas Launcher. An optional **Auto Update** switch installs new versions on startup, and the release notes are shown for 15 seconds after an update.
- **Live Port Status:** Built-in tracker powered by [Portwatch VI](https://portwatch-vi.xyz/).

## Supported Runners

Runners are configured in [`src/AmericasLauncher/Data/runners.json`](src/AmericasLauncher/Data/runners.json). Click any of the 9 runner cards on the **Launch** screen to switch between them. The "Best with" column is the hint shown on each card.

| Runner | Based On | Best with | Repository |
|---|---|---|---|
| **XCL** | Independent Xbox Series compatibility layer | Both | [xcl-dev/xcl](https://github.com/xcl-dev/xcl) |
| **Belabug - VIctory Based** | VIctory fork | AMD | [Belobug/VIctory-Belobug-AMD](https://github.com/Belobug/VIctory-Belobug-AMD) |
| **Dexnom - VIctory Based** | VIctory fork | Both | [DexNom/VIctory](https://github.com/DexNom/VIctory) |
| **KLBF-LAB** | VIctory fork | AMD | [PastiiJay/klbf-lab](https://github.com/PastiiJay/klbf-lab) |
| **VIctory(Original)** | Original VIctory compatibility layer | Unknown | [Marvisak/VIctory](https://github.com/Marvisak/VIctory) |
| **hyphenn - VIctory Based** | VIctory fork | Unknown | [fxceless/victory-fork](https://github.com/fxceless/victory-fork) |
| **AvatarManz - VIctory Based** | VIctory fork | AMD | [avatarmanz/VIctory-7900XTX](https://github.com/avatarmanz/VIctory-7900XTX) |
| **Nvidia - VIctory Based** | VIctory fork (based on AvatarManz) | Nvidia | [trustno1-coder/VIctory-NVIDIA](https://github.com/trustno1-coder/VIctory-NVIDIA) |
| **CGBJ - Victory Based** | VIctory fork | Nvidia | [DoubleGhosty/cool_fox](https://github.com/DoubleGhosty/cool_fox) |

Runners are third-party projects that the launcher builds from source on your PC. Review a runner's code before you run it.

## Port Status

The **Port Status** tab fetches live data directly from **[Portwatch VI](https://portwatch-vi.xyz/)** ([`4lwayz/portwatch-vi`](https://github.com/4lwayz/portwatch-vi)):

![Americas Launcher - Port Status](docs/images/port-status.jpg)

- **Overall progress · closest effort to playable:** Shows the leading build, overall percentage score, top-3 average, milestone progress (`Groundwork` → `Exe runs` → `First frame` → `Main menu` → `In the world` → `Playable` → `Complete`), and the four quality tracks (`Story progress`, `Rendering`, `Performance`, `Stability`).
- **What changed:** Displays the latest tracked updates and newly discovered community efforts.

## Settings

![Americas Launcher - Settings](docs/images/settings-view.png)

- **Game Folder & Game Executable:** Select your game directory and `.exe` file once; the launcher remembers them automatically.
- **Storage & Cache:** View how much disk space each installed runner and the VIctory shader/pipeline cache (`%LocalAppData%\victory\cache`) use, and delete any runner or clear the pipeline cache with one click.
- **In-App Updates:** Click **Check for Updates** to check GitHub Releases. When a new version is available, click **Update Now** to download, extract, and restart into the latest version automatically without visiting GitHub.

## Requirements

- Windows 10/11 (64-bit) with [winget](https://learn.microsoft.com/windows/package-manager/) (App Installer)
- A D3D12 GPU with Shader Model 6.6 and mesh shaders
- Several GB of free disk space and an internet connection for the first setup (Visual Studio Build Tools and Rust are large)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build the launcher from source, or use a release build

## Building the Launcher

```powershell
git clone https://github.com/isaac-btw/AmericasLauncher.git
cd AmericasLauncher/src/AmericasLauncher
dotnet run
```

To create a release build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

## Usage

1. Open **Settings** from the left sidebar and select your **Game Folder** and **Game Executable** (`Game.exe`).
2. Go to **Launch** and click the runner card you want to use.
3. Click **Install / Update** to install dependencies and build the runner. Watch the integrated log box at the bottom.
4. Click **Launch Game**. *(First-time shader/pipeline translation can take several minutes before the game window opens.)*
5. Click **Stop Game** (the same button) whenever you want to cancel — even while the pipeline translation is still running.

Launcher configuration and built runner sources are stored in `%LocalAppData%\AmericasLauncher\`.

## Adding a Runner

Add an entry to `src/AmericasLauncher/Data/runners.json`:

```json
{
  "id": "example",
  "name": "Example",
  "gitUrl": "https://github.com/owner/repo.git",
  "dependencies": [
    { "name": "Git", "wingetId": "Git.Git", "check": "command:git" }
  ],
  "buildCommands": [ "cargo build --release" ],
  "exe": "target\\release\\example.exe",
  "preLaunchArgs": [ "translate --pipelines \"{gameFolder}\\shaders.rpf\" \"{gameExe}\"" ],
  "launchArgs": "run \"{gameExe}\" --execute",
  "notes": "Optional text shown after a successful setup."
}
```

| Field | Description |
|---|---|
| `gitUrl` | Repository cloned into `%LocalAppData%\AmericasLauncher\sources\<id>` |
| `dependencies` | `winget` packages to install if missing (`command:<exe>` or `vsbuildtools`) |
| `buildCommands` | Commands run with `cmd.exe` inside the source folder |
| `exe` | Built executable, relative to the source folder |
| `preLaunchArgs` | Optional list of commands run sequentially before `launchArgs` |
| `launchArgs` | Arguments for the executable (`{gameFolder}` and `{gameExe}` are replaced automatically) |
| `benchmarkEnv` | Optional: environment variables that turn on the runner's own built-in benchmark. When set, a **Benchmark** button appears next to Launch Game and starts the game with them added. |

## License

[GNU GPL v2](LICENSE) (`GPL-2.0-only`).

## Disclaimer

Americas Launcher is an independent open-source project and has **no affiliation, association, authorization, endorsement, or connection whatsoever with Grand Theft Auto (GTA), Rockstar Games, or Take-Two Interactive**. It is also not affiliated with the developers of XCL, VIctory, or Portwatch VI — those projects are developed separately under their own licenses. Use of pirated or leaked content is not supported.
