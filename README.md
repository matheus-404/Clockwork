# ⚙️ Clockwork

![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg) ![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg) [![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://opensource.org/licenses/MIT) ![Release](https://img.shields.io/github/v/release/matheus-404/Clockwork?include_prereleases) 

**Clockwork** is a lightweight Windows system-monitoring application built with **C#**, **.NET 10**, and **Avalonia UI**. It provides an in-game telemetry overlay focused on frame-rate, frame-time, latency, CPU, GPU, RAM, and session information, while keeping the application itself simple, unobtrusive, and conscious of the additional access that hardware-monitoring tools can introduce.

The name **Clockwork** is inspired by *...Like Clockwork* by **Queens of the Stone Age**.

---

## ✨ Highlights

- Clean, dark desktop interface with a restrained graphite/periwinkle visual system.
- Configurable in-game overlay with persistent position, scale, background, and opacity settings.
- Frame-rate monitoring including FPS, average FPS, 1% low FPS, 0.1% low FPS, and frame time.
- PresentMon-backed GPU, frame-pacing, and latency telemetry.
- Per-logical-processor CPU frequency monitoring through Windows Performance Data Helper (PDH).
- RAM usage and game/process working-set monitoring.
- Session playtime and system clock information.
- Automatic detection of fullscreen applications/games without opening the game process for memory access.
- Two-stage telemetry loop: a slower session/process loop and a high-frequency frame/latency loop that only runs when required.
- Settings are persisted locally so selected statistics do not need to be configured again after every launch.
- System-tray operation: closing the main window hides Clockwork to the tray; **Close Clockwork** in the tray menu performs the real shutdown.
- Automatic PresentMon installation/update from the official PresentMon GitHub release feed.
- SHA-256 verification of downloaded PresentMon installers when GitHub publishes a digest.
- x64 Windows target and a self-contained deployment path suitable for Velopack packaging.

---

## 🖥️ What Clockwork Does

Clockwork consists of two primary parts:

1. **The control application** — where statistics are enabled, overlay behavior is configured, and the current configuration is saved.
2. **The in-game overlay** — a small, click-through window that appears only when a tracked fullscreen game/application is active and visible.

The overlay is intentionally separate from the settings UI, so the monitoring display can stay minimal while configuration remains available from the main application window.

---

## 📊 Available Statistics

Clockwork currently exposes **43 configurable statistics/options** across six metric categories plus overlay settings.

### Performance

| Statistic | Description |
|---|---|
| **FPS** | Current frame rate calculated from recent frame timings. |
| **Avg FPS** | Average frame rate over the current session measurement. |
| **1% Low FPS** | Time-based 1% low frame-rate value. |
| **0.1% Low FPS** | Time-based 0.1% low frame-rate value. |
| **Frame Time** | Average recent frame time in milliseconds. |
| **Dropped Frames** | PresentMon drop-frame state/value when available. |
| **Presented FPS** | Presentation rate reported by PresentMon. |
| **Displayed FPS** | Displayed frame rate reported by PresentMon. |
| **Application FPS** | Application-level frame rate reported by PresentMon. |

### CPU

| Statistic | Description |
|---|---|
| **CPU Frequency** | Frequency for each logical processor, reported individually. |
| **CPU Usage** | CPU utilization percentage. |
| **CPU Busy** | CPU busy time associated with the tracked frame/telemetry data. |
| **CPU Wait** | CPU wait time associated with the tracked frame/telemetry data. |
| **CPU Frame Time** | CPU-side frame time in milliseconds. |

CPU frequency is collected separately from the PresentMon query using Windows PDH, while the other CPU telemetry is PresentMon-backed.

### GPU

| Statistic | Description |
|---|---|
| **GPU Temperature** | GPU temperature in °C. |
| **GPU Core Clock Frequency** | GPU core clock in MHz. |
| **GPU Memory Clock Frequency** | GPU memory clock in MHz. |
| **GPU VRAM Usage** | Used video memory in MB. |
| **VRAM Usage (%)** | VRAM utilization percentage. |
| **GPU Power** | GPU power consumption in watts. |
| **GPU Usage** | GPU utilization percentage. |
| **GPU Render/Compute Utilization** | PresentMon render/compute utilization percentage when available. |
| **GPU Power Limited** | Whether PresentMon reports a power-limiting condition. |
| **GPU Temperature Limited** | Whether PresentMon reports a temperature-limiting condition. |
| **GPU Current Limited** | Whether PresentMon reports a current-limiting condition. |
| **GPU Voltage Limited** | Whether PresentMon reports a voltage-limiting condition. |
| **GPU Utilization Limited** | Whether PresentMon reports a utilization-limiting condition. |
| **GPU Busy** | GPU busy time in milliseconds. |
| **GPU Wait** | GPU wait time in milliseconds. |
| **GPU Time** | GPU time in milliseconds. |

Actual metric availability depends on the installed PresentMon version, graphics stack, device, driver, and the telemetry exposed by the system.

### RAM

| Statistic | Description |
|---|---|
| **RAM Usage** | Used system memory versus total system memory. |
| **RAM Usage (%)** | Percentage of system memory currently in use. |
| **Process/Game RAM Usage** | Working-set memory of the tracked foreground game/application. |

### Latency

| Statistic | Description |
|---|---|
| **GPU Latency** | GPU latency reported by PresentMon. |
| **Display Latency** | Display latency reported by PresentMon. |
| **Render/Present Latency** | Render-to-present latency reported by PresentMon. |
| **Time Until Displayed** | Time between presentation and display as exposed by PresentMon. |
| **Between Presents** | Interval between present events. |
| **Between Display Changes** | Interval between display changes. |
| **Click-to-Photon Latency** | Input click-to-photon latency when supported by the PresentMon data source. |
| **All Input-to-Photon Latency** | Input-to-photon latency covering all supported input events. |

Latency and other high-frequency frame metrics use a dedicated fast telemetry path so they can update frequently without forcing the entire application into the highest polling rate all the time.

### More

| Statistic | Description |
|---|---|
| **System Time** | Current local system time in `HH:mm` format. |
| **Session Playtime** | Time elapsed since the current tracked game/application session began. |

---

## 🎯 FPS, Frame Time, and Low-FPS Calculations

Clockwork keeps a bounded history of up to **20,000 frame times** in a preallocated ring buffer. This avoids continuously growing allocations while the overlay is running.

### FPS

Current FPS is calculated from recent frame timings covering approximately the latest one-second window:

```text
FPS = frame_count / elapsed_time
```

where elapsed time is represented by the sum of the collected frame durations.

### Average FPS

The session average uses the number of measured frames divided by the elapsed session measurement time.

### 1% Low and 0.1% Low

The low-FPS implementation uses a **time-based percentile approach**. Frame times are sorted from worst to best, and the slowest frame times are accumulated until they account for the requested fraction of the total measured frame time. The frame time at that boundary is converted back into FPS.

This is intentionally different from simply averaging the slowest 1% or 0.1% of frames. The implementation was chosen to follow the same general time-based low-FPS methodology associated with MSI Afterburner/RTSS-style reporting; it should not be interpreted as a guarantee of bit-for-bit numerical identity with another monitoring application.

---

## 🎮 Game / Application Detection

Clockwork periodically checks the foreground desktop state to determine whether a suitable fullscreen application is active.

The process table is collected through the native Windows `NtQuerySystemInformation(SystemProcessInformation)` process snapshot interface, and window/monitor state is obtained through Win32 APIs.

The detector considers information such as:

- process ID
- executable/process name
- executable path when available
- main window handle
- monitor bounds
- fullscreen coverage
- foreground state
- window visibility
- minimized state

A tracked session is maintained through temporary process-table misses so that a short refresh failure does not immediately tear down an active overlay session.

Clockwork also maintains a large ignore list for applications where an in-game monitoring overlay would not be useful, such as hardware monitors, launchers, editors, terminals, media players, and other desktop tools.

---

## 🛡️ Anticheat-Conscious Design

Clockwork is designed to be **non-invasive** toward the game/application being monitored.

The current architecture does **not intentionally implement game-memory inspection or code injection**. The project does not use a game-process handle to read or write process memory, does not use `ReadProcessMemory`/`WriteProcessMemory`, and does not inject DLLs or create remote threads in the tracked application.

Instead:

- Process discovery uses a system process snapshot.
- Window information is retrieved through normal Win32 window APIs.
- Performance telemetry is obtained primarily through PresentMon.
- Per-CPU frequency is obtained from the Windows PDH subsystem.
- The overlay itself is a separate top-level window configured with click-through/no-activate/tool-window extended styles.

### Important disclaimer

No third-party monitoring application can honestly guarantee compatibility with every anti-cheat product or every future game update. Clockwork is **anticheat-conscious by architecture**, not an anti-cheat compatibility guarantee.

If a particular game or anti-cheat system blocks PresentMon or another telemetry source, Clockwork cannot override that restriction safely.

---

## 📡 PresentMon Dependency

Clockwork uses **PresentMon** as its main performance telemetry backend.

PresentMon is maintained separately from Clockwork. Clockwork does not bundle a hardcoded PresentMon version into its source code or depend on a manually maintained version number for its update logic.

The current startup flow is:

```text
Clockwork starts
      │
      ▼
Check official PresentMon release information
      │
      ├── Internet unavailable + PresentMon installed
      │       └── Continue using installed PresentMon
      │
      ├── Internet unavailable + PresentMon missing
      │       └── Explain that PresentMon is required
      │
      ├── Installed version is current
      │       └── Continue normally
      │
      └── Newer release available
              └── Download → verify → install → continue
```

The current updater retrieves the latest release from the official PresentMon GitHub release API:

```text
https://api.github.com/repos/GameTechDev/PresentMon/releases/latest
```

It then:

1. Reads the installed `PresentMonAPI2.dll` file version.
2. Reads the latest official release tag.
3. Compares the installed and latest release versions.
4. Selects an MSI, preferring an x64 MSI when one is available.
5. Downloads the installer over HTTPS from `github.com`.
6. Verifies the SHA-256 digest when the GitHub release supplies one.
7. Installs it through Windows Installer (`msiexec.exe`) using a silent, no-restart installation.
8. Re-checks the installed version after installation.

The current source requires **PresentMon API major version 3** and reports **PresentMon Service 2.3.1 or newer** when the required API DLL cannot be found.

### Official PresentMon resources

- PresentMon repository: https://github.com/GameTechDev/PresentMon
- PresentMon releases: https://github.com/GameTechDev/PresentMon/releases

---

## 🖥️ Overlay

The live overlay is intentionally kept visually distinct from the control application.

The overlay supports:

- configurable horizontal position
- configurable vertical position
- configurable scale
- optional solid black background
- configurable background opacity
- click-through interaction
- no input focus
- hidden-from-task-switcher behavior
- automatic visibility based on the tracked application

### Positioning

Horizontal and vertical positions are stored as percentages of the tracked application's client/monitor area. This makes the overlay position more consistent across different resolutions and display modes.

The default position is:

```text
X = 0%
Y = 0%
```

The settings UI also contains a live draggable preview.

### Scale

The overlay scale is stored as a percentage and converted into a layout scale at runtime. Reapplying the same scale is avoided through cached state so the overlay does not repeatedly rebuild the same transform.

### Appearance

The live overlay has its own visual styling and is intentionally kept separate from the main application's theme. Changing the application's graphite/periwinkle UI palette does not alter the live overlay design.

---

## 🔔 System Tray Behavior

Clockwork remains resident in the system tray when the main window is closed.

### Main window close button

Clicking the normal **X** does not terminate the process. Instead:

```text
X
 ↓
Hide main window
 ↓
Remove from taskbar
 ↓
Remain in system tray
```

### Tray interactions

Left-clicking the tray icon restores Clockwork.

Right-clicking the tray icon provides:

- **Open Clockwork**
- **Close Clockwork**

**Close Clockwork** performs the actual application shutdown.

Application/OS shutdown paths are still allowed to close the application normally.

This behavior is implemented using Avalonia's tray support and an explicit desktop shutdown mode so that hiding the window does not terminate the process.

---

## 💾 Settings and Persistence

Clockwork stores user settings outside the application installation directory, under the user's local application-data area.

This includes:

- enabled/disabled statistics
- overlay X position
- overlay Y position
- overlay scale
- overlay background enabled state
- overlay background opacity

Settings writes are **debounced** rather than written immediately for every individual UI change. The pending state is flushed when the application exits.

---

## ⚡ Performance Architecture

Clockwork is designed so that expensive telemetry work does not run unnecessarily on the UI thread.

### Two telemetry loops

**Slow telemetry loop:** approximately every **500 ms**.

Used for tasks such as:

- application/game detection
- session transitions
- normal telemetry polling
- overlay visibility decisions

**Fast telemetry loop:** approximately every **20 ms**, but only while high-frequency frame/latency statistics require it.

Used for:

- current FPS
- GPU latency
- display latency
- render/present latency
- other frame-level latency statistics

When no fast statistics are enabled, the fast loop waits rather than continuously polling.

### Enabled-stat planning

The enabled statistic set is cached. Clockwork derives a PresentMon metric plan from that set and only registers/queries telemetry that is currently needed.

Changing the selected statistics causes the relevant telemetry plan to be rebuilt rather than keeping every possible PresentMon metric active all the time.

### Frame history

Frame timing uses a fixed-size `double[20000]` ring buffer and maintains a running sum. This reduces repeated list allocations and makes average/low-FPS calculations more predictable.

### UI updates

Telemetry collection occurs on worker tasks. Only the visual update portion is posted back to Avalonia's UI dispatcher.

---

## 🧩 Technology Stack

| Component | Technology |
|---|---|
| Language | C# |
| Runtime | .NET 10 |
| UI framework | Avalonia UI 12.1.3 |
| Rendering | Skia |
| Text shaping | HarfBuzz |
| Platform | Windows x64 |
| GPU/frame telemetry | PresentMon |
| CPU frequency telemetry | Windows PDH |
| Native interop | Win32 / P/Invoke |
| Planned application packaging | Velopack |
| Planned release distribution | GitHub Releases + GitHub Actions |

The current project explicitly targets x64 and uses explicit Avalonia Win32/Skia/HarfBuzz backends rather than the generic platform-detection setup.

---

## 📁 Project Structure

```text
Clockwork/
├── Assets/
│   └── Icons/
│       ├── Clockwork.ico
│       └── *.svg
│
├── Controls/
│   └── AspectRatioBorder.cs
│
├── Converters/
│   └── SvgAssetValueConverter.cs
│
├── Overlay/
│   ├── IgnoredApplications.cs
│   ├── OverlayController.cs
│   ├── OverlayViewModel.cs
│   ├── OverlayWindow.axaml
│   ├── OverlayWindow.axaml.cs
│   └── Win32.cs
│
├── Services/
│   ├── PresentMonMonitor.cs
│   ├── PresentMonNative.cs
│   ├── PresentMonUpdateService.cs
│   ├── ProcessorFrequencyMonitor.cs
│   ├── SettingsService.cs
│   └── StartupMessageBox.cs
│
├── ViewModels/
│   ├── MainWindowViewModel.cs
│   ├── OptionViewModel.cs
│   ├── SectionViewModel.cs
│   └── ViewModelBase.cs
│
├── Views/
│   ├── MainWindow.axaml
│   └── MainWindow.axaml.cs
│
├── App.axaml
├── App.axaml.cs
├── Clockwork.csproj
├── Clockwork.slnx
├── Program.cs
└── app.manifest
```

### Important classes

#### `OverlayController`

The central orchestration layer for game detection, telemetry, session state, enabled-stat planning, overlay updates, and background worker loops.

#### `PresentMonMonitor`

Owns the PresentMon API session, introspection, dynamic/frame queries, telemetry binding, frame history, FPS calculation, low-FPS calculation, and GPU/device selection.

#### `PresentMonNative`

Contains the native PresentMon API bindings, structures, delegates, dynamic library loading logic, and compatibility search paths for `PresentMonAPI2.dll`.

#### `ProcessorFrequencyMonitor`

Uses Windows PDH to collect per-logical-processor frequency data independently of PresentMon.

#### `PresentMonUpdateService`

Keeps the separately installed PresentMon dependency current without a hardcoded release version. It also handles offline and installation-failure states.

#### `SettingsService`

Loads and saves Clockwork's user settings in the user's local application-data directory.

#### `MainWindowViewModel`

Defines the available sections/statistics and manages their persisted UI state and overlay settings.

#### `MainWindow`

Contains the configuration UI, overlay-position preview, custom title-bar behavior, resize handling, and tray/minimize-to-tray integration hooks.

---

## 🔧 Requirements

### Runtime / OS

The current project targets:

```text
Windows x64
.NET 10
```

Because the application targets `net10.0-windows`, compatibility should be evaluated against the Windows versions supported by the .NET 10 runtime as well as the graphics/runtime requirements of Avalonia.

---

## 🔐 Distribution / Security Notes

Clockwork's release system should follow a few basic rules:

- Only use the official PresentMon GitHub repository as the PresentMon update source.
- Only accept HTTPS downloads for PresentMon assets.
- Restrict downloaded PresentMon installer URLs to `github.com`.
- Verify a release digest when one is provided.
- Do not embed a privileged GitHub token inside the distributed Clockwork application.
- Keep Clockwork's settings outside the application installation directory.
- Do not add process-memory manipulation merely to obtain a statistic that can be sourced elsewhere.

Velopack will be responsible for Clockwork packages; PresentMon's official MSI remains an external dependency rather than being silently repackaged into Clockwork.

---

## 🐛 Troubleshooting

### Clockwork opens but performance metrics are unavailable

Check that PresentMon is installed and that its `PresentMonAPI2.dll` is discoverable. Clockwork will try the PresentMon installation/update flow at startup and will report failures through the startup message box.

### PresentMon cannot be updated

The updater requires network access to the official PresentMon GitHub release feed and downloads the official MSI over HTTPS. If no Internet connection is available, Clockwork uses the installed version when possible.

### The overlay does not appear

Check:

- a compatible fullscreen application is actually in the foreground;
- the application is not in Clockwork's ignore list;
- the desired statistic is enabled;
- PresentMon is available for the metrics being requested.

### Settings reset after changing options

Clockwork debounces settings saves and flushes them on shutdown. If settings are lost repeatedly, inspect the user's local application-data directory and any filesystem permissions affecting the Clockwork settings file.

### Why does a particular PresentMon statistic show `N/A`?

PresentMon metrics are dependent on the API's introspected metric set and the telemetry exposed by the current graphics/system environment. A statistic can exist in the Clockwork UI while remaining unavailable on a particular machine.

---

## 🤝 Contributing

Contributions, bug reports, and technical feedback are welcome.

When submitting an issue, useful information includes:

- Windows version/build
- GPU model and driver version
- CPU model
- PresentMon version
- whether Clockwork was installed or run from a development build
- which statistic or feature is affected
- whether the issue reproduces with only the relevant statistic enabled

For performance-related issues, avoid attaching private telemetry or system information that is unrelated to the problem.

---

## 🔗 Useful Links

- Avalonia UI: https://avaloniaui.net/
- .NET: https://dotnet.microsoft.com/
- PresentMon: https://github.com/GameTechDev/PresentMon
- Velopack: https://velopack.io/
- Velopack documentation: https://docs.velopack.io/

---

## ❤️ Philosophy

Clockwork is intended to be a monitoring tool that gets out of the way.

The ideal experience is simple:

```text
Launch Clockwork
      ↓
Choose the statistics you care about
      ↓
Start a game
      ↓
See the information you need
      ↓
Forget that Clockwork is even there
```

No unnecessary overlays, no huge monitoring suite, no requirement to configure every statistic every time, and no reason to interact with the monitored application's memory.

---

**Clockwork** — lightweight telemetry, clean presentation, and as little interference as practical. ⚙️