# ⚙️ Clockwork

![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg) ![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg) [![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://opensource.org/licenses/MIT) ![Release](https://img.shields.io/github/v/release/matheus-404/Clockwork?include_prereleases)

**Clockwork** is a lightweight Windows system-monitoring application built with **C#**, **.NET 10**, and **Avalonia UI**. It provides an in-game telemetry overlay focused on frame rate, frame time, latency, CPU, GPU, RAM, and session metrics, while keeping the application simple, unobtrusive, and conscious of anti-cheat integrity.

The name **Clockwork** is inspired by *...Like Clockwork* by **Queens of the Stone Age**.

---

## ✨ Highlights

* Clean, dark desktop interface with a restrained graphite visual system.
* Configurable in-game overlay with persistent position, scale, background, and opacity settings, plus mixed-DPI multi-monitor placement accuracy.
* Global hotkey support (default `Ctrl + Shift + O`) to toggle overlay visibility in-game without hooking into game processes.
* Frame-rate monitoring including live FPS, average FPS, 1% low FPS, 0.1% low FPS, and frame time, with primary swap-chain filtering so secondary windows or overlays do not skew readings.
* Time-based exponential moving average (EMA, τ = 250 ms) smoothing for per-frame events to eliminate single-sample jitter in CPU/GPU busy, wait, and latency stats regardless of polling rate.
* PresentMon API v3-backed GPU, frame-pacing, and latency telemetry, with automatic active-adapter selection for hybrid iGPU + dGPU systems.
* Workload-focused CPU telemetry avoiding kernel-level drivers (no PawnIO or custom kernel modules).
* RAM usage and private working-set memory monitoring.
* Session playtime and system clock information.
* Automatic detection of foreground fullscreen and borderless games with a 2-second focus debounce filter to prevent Alt-Tab resets.
* Customizable ignore list allowing users to extend the built-in application blacklist.
* Windowed games supported via an opt-in toggle.
* Three decoupled, low-overhead background loops:
  * **Detection loop** (500 ms) for foreground tracking and window state, hibernating when all statistics are off or when the overlay is hidden with no tracked game.
  * **Telemetry loop** (100 ms, throttled to 500 ms when hidden) for CPU, GPU, system RAM, and session timers, parked when no game is tracked and skipping UI formatting/marshaling while hidden.
  * **Fast frame loop** (16 ms ETW drain, 100 ms UI publish) active only while the overlay is visible and high-frequency frame or latency metrics are enabled.
* Debounced local settings persistence in `%LocalAppData%\Clockwork` with atomic writes and automatic `.bak` recovery against corrupted files.
* System tray integration with startup options (Start with Windows, Start Minimized) and first-run minimize hints.
* Secure PresentMon service installer: minimum-version gating, TOCTOU swap protection via read-locking, strict Authenticode certificate chain verification, and quiet handling of rate limits / offline starts.
* Background application update checks powered by Velopack on a recurring 24-hour cycle for persistent tray sessions, with scheduled-version deduplication.
* Self-contained x64 Windows build trimmed with invariant globalization and English satellite resource filtering.

---

## 🖥️ What Clockwork Does

Clockwork consists of two primary components:

1. **The Control Window** — configure active metrics, adjust overlay position/scale/styling via a live interactive preview, set global toggle hotkeys, extend the process blacklist, and configure startup preferences.
2. **The In-Game Overlay** — a hardware-accelerated, transparent, click-through (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`) overlay positioned over the game's client area using per-monitor effective DPI scaling (`GetDpiForMonitor`) and aligned tabular columns.

By default, the overlay activates only over foreground games that cover their monitor (fullscreen exclusive or borderless windowed). Floating windowed games can be tracked by enabling **Include Windowed Games** in the Overlay settings.

---

## 📊 Available Statistics

Clockwork provides **41 configurable statistics** organized across six categories, with only **FPS** enabled by default:

### Performance

| Statistic | Description |
| --- | --- |
| **FPS** | Live frame rate calculated over the preceding 1-second window. |
| **Avg FPS** | Running average frame rate across the active session (idle gaps ≥2 s excluded). |
| **1% Low FPS** | Average frame rate of the slowest 1% of frames across the last 10 seconds (gaps ≥2 s excluded). |
| **0.1% Low FPS** | Average frame rate of the slowest 0.1% of frames across the last 10 seconds (gaps ≥2 s excluded). |
| **Frame Time** | Average recent frame time in milliseconds (1-second window). |
| **Dropped Frames** | Indicates whether frames were dropped across the publish window (`Yes`/`No`). |
| **Presented FPS** | Presentation rate reported by PresentMon. |
| **Displayed FPS** | Displayed frame rate reported by PresentMon. |
| **Application FPS** | Application-level render rate reported by PresentMon (`N/A` if unavailable; does not duplicate presented rate during frame generation). |

### CPU

| Statistic | Description |
| --- | --- |
| **CPU Usage** | Total CPU utilization percentage. |
| **CPU Busy** | CPU execution time per frame before waiting (ms, time-smoothed). |
| **CPU Wait** | CPU wait time per frame (ms, time-smoothed). |
| **CPU Frame Time** | Total CPU-side frame processing time (ms, time-smoothed). |

*Note: Clockwork intentionally does not read CPU clock frequencies, per-core temperatures, or voltages. Doing so reliably on Windows requires kernel-level hardware-access drivers (such as PawnIO or WinRing0). Clockwork stays strictly in user space to maintain stability and anti-cheat compatibility.*

### GPU

| Statistic | Description |
| --- | --- |
| **GPU Temperature** | GPU core temperature (°C). |
| **GPU Core Clock Frequency** | GPU core clock frequency (MHz). |
| **GPU Memory Clock Frequency** | GPU memory clock frequency (MHz). |
| **GPU VRAM Usage** | Dedicated video memory used (MB). |
| **VRAM Usage (%)** | Video memory utilization percentage. |
| **GPU Power** | Total board/package power draw (W). |
| **GPU Usage** | Overall GPU utilization percentage. |
| **GPU Render/Compute Utilization** | Specialized render/compute pipeline utilization (%). |
| **GPU Power Limited** | Indicates if GPU performance is limited by power (`Yes`/`No`). |
| **GPU Temperature Limited** | Indicates if GPU performance is limited by thermal throttling (`Yes`/`No`). |
| **GPU Current Limited** | Indicates if GPU performance is limited by electrical current (`Yes`/`No`). |
| **GPU Voltage Limited** | Indicates if GPU performance is limited by voltage limits (`Yes`/`No`). |
| **GPU Utilization Limited** | Indicates if GPU performance is limited by utilization ceilings (`Yes`/`No`). |
| **GPU Busy** | GPU execution time per frame (ms, time-smoothed). |
| **GPU Wait** | GPU idle/wait time per frame (ms, time-smoothed). |
| **GPU Time** | Total GPU processing duration per frame (ms, time-smoothed). |

### RAM

| Statistic | Description |
| --- | --- |
| **RAM Usage** | System physical memory used vs. total (`used / total GB`). |
| **RAM Usage (%)** | System memory utilization percentage. |
| **Process/Game RAM Usage** | Private working set of the tracked game process (GB). |

### Latency

| Statistic | Description |
| --- | --- |
| **GPU Latency** | Time between GPU work submission and completion (ms, time-smoothed). |
| **Display Latency** | Time between presentation and display scanout (ms, time-smoothed). |
| **Render/Present Latency** | Total latency from render start to present call (ms, time-smoothed). |
| **Time Until Displayed** | Duration a presented frame waits until appearing on screen (ms, time-smoothed). |
| **Between Presents** | Interval between consecutive present calls (ms, time-smoothed). |
| **Between Display Changes** | Interval between actual display refreshes (ms, time-smoothed). |
| **Input-to-Photon Latency** | End-to-end input-to-display latency across supported input events (ms, time-smoothed). |

### More

| Statistic | Description |
| --- | --- |
| **System Time** | Current local clock time in `HH:mm`. |
| **Session Playtime** | Elapsed session duration in `HH:mm:ss`. |

---

## 🎯 Calculation Methodology

### Frame Rate, Frame Time & Primary Swap-Chain Filtering

A ring buffer (`FrameStatistics`) holds up to **65,536 recent frame times** with high-resolution timestamps (`Stopwatch` ticks). Because a single process may present on multiple swap chains (e.g., launchers, loading screens, or in-process overlays), Clockwork tracks `SWAP_CHAIN_ADDRESS` and filters frame events to the busiest active swap chain:

* **Live FPS**: Calculated as `1000.0 * frame_count / elapsed_ms` over the trailing 1,000 ms window.
* **Frame Time**: Arithmetic mean of all frame durations within the trailing 1,000 ms window.
* **Data Stale Timeout**: If no new frame arrives within 2,000 ms (e.g., loading screens or pause menus), calculations return `N/A` rather than holding an old value.

### 1% and 0.1% Lows

Lows are recalculated once per second across a **10-second rolling window**:

1. All frame durations from the last 10 seconds are filtered to exclude gaps ≥2,000 ms (such as loading screens and pauses), aligning with Average FPS.
2. When the primary swap chain switches (such as transitioning from a loading-screen swap chain to gameplay), the ring buffer history is cleared automatically so slow transition frames do not linger in the lows window.
3. The samples are sorted in ascending order outside the telemetry lock.
4. Percentile slicing uses rounded counts (`Math.Round(count * fraction)`), allowing 1% lows to scale continuously and 0.1% lows to activate from 500 valid samples onward.
5. The average duration of the slowest slice is converted to FPS: `1000.0 / average_slow_frame_time_ms`.

### Average FPS

The running session average tracks total active frames divided by total active time. Frame durations ≥ 2,000 ms are filtered out as idle gaps (such as Alt-Tab or loading screens) so they do not artificially depress the session average, and swap-chain transitions preserve the running session average.

### Time-Based Frame Metric Smoothing & Multi-GPU Selection

* **Time-Based EMA (τ = 250 ms)**: Per-frame metrics (`CPU Busy`, `GPU Busy`, `Frame Time`, and latency values) are smoothed using `alpha = 1 - exp(-dt / tau)` across each drain cycle, ensuring identical responsiveness whether frames are drained every 16 ms (fast loop) or 100 ms (normal loop).
* **Hybrid GPU Selection**: Whenever any GPU statistic is enabled, Clockwork automatically polls adapter utilization behind the scenes and selects the active graphics adapter that supports the requested metrics (using utilization, render/compute load, power, core clock, and VRAM as tie-breakers), ensuring iGPU + dGPU laptops report telemetry from the active gaming GPU.

---

## 🎮 Game Detection & Session Tracking

Clockwork detects games without installing system hooks:

1. **Foreground Candidate Query**: Evaluates the foreground window using Win32 API calls (`GetForegroundWindow`, `GetWindowRect`, `GetClientRect`, `ClientToScreen`).
2. **Debounce Gate**: Candidate foreground windows must remain active for at least 2 seconds before Clockwork switches tracked processes, preventing temporary focus changes (like notifications or brief Alt-Tabs) from resetting statistics.
3. **Monitor Boundary & DPI Check**: Verifies whether the client rect covers the nearest display monitor (within a 4-pixel tolerance) and queries the target monitor's effective DPI (`GetDpiForMonitor`) for pixel-accurate overlay placement. If **Include Windowed Games** is checked, floating windows with a minimum size of 320 x 240 qualify as well.
4. **Blacklist Filtering**: System components, browsers, chat tools, IDEs, media players, streaming tools, and other monitoring software are filtered out via executable name comparison. Users can append custom executable names in settings.
5. **Process Tracking**: Reads the process ID, creation time, and private working set through `NtQuerySystemInformation(SystemProcessInformation)`.
6. **Session Persistence**: Sessions are indexed by `(Pid, CreateTime)`. When you Alt-Tab away from a game and return, session playtime and running statistics continue uninterrupted.

---

## 🛡️ Non-Invasive Anti-Cheat Design

Clockwork does not hook DirectX/Vulkan/OpenGL runtimes and does not inject code into running games:

* No DLL injection (`CreateRemoteThread`, `SetWindowsHookEx`, `AppInit_DLLs`).
* No process memory reading or writing (`OpenProcess` with memory access, `ReadProcessMemory`, `WriteProcessMemory`).
* No custom kernel-mode drivers.
* Overlay rendering is handled by an external Avalonia window positioned directly over the game's client coordinates using Win32 extended styles.
* Topmost state is periodically re-asserted by the overlay controller to ensure visibility over borderless games without stealing input focus.
* Global hotkey toggling uses standard Windows `RegisterHotKey` hooks without game hook dependencies.
* Frame and GPU telemetry are gathered via ETW through the official Intel PresentMon Service.

---

## 📡 PresentMon & App Updates

### PresentMon Service Dependency

Clockwork utilizes the **PresentMon API v3** (`PresentMonAPI2.dll`). On startup, `PresentMonUpdateService` manages the dependency safely:

1. Scans standard installation paths (`%ProgramFiles%\Intel\PresentMonSharedService`, `%ProgramFiles%\Intel\PresentMon`, etc.).
2. Verifies the installed version against `MinimumSupportedVersion` (2.3.1). If a compatible version is present, update checks are skipped to prevent unnecessary UAC prompts and ABI churn.
3. If missing or below 2.3.1, checks official GitHub releases for `GameTechDev/PresentMon`. Offline states and GitHub API rate limits (HTTP 403) are handled silently without popup modals.
4. Validates the SHA-256 digest against the release checksum file or release digest.
5. **MSI Swap Mitigation**: The installer file in `%TEMP%` is kept locked with an open read-only `FileStream` (`FileShare.Read`) from the initial SHA-256 verification through the completion of `msiexec.exe`, closing the window for unprivileged same-user file replacement.
6. **Authenticode Verification**: Validates the full certificate trust chain using `WinVerifyTrust` and confirms the publisher organization (`O`) is exactly `"Intel Corporation"`.
7. Executes `msiexec.exe /i <path> /qn /norestart` via an administrative prompt.
8. Validates the introspection struct layouts against the PresentMon C ABI before registering telemetry queries.

### Application Updates (Velopack)

Clockwork checks for GitHub releases via **Velopack**. Beyond startup checks, an automatic 24-hour background timer monitors for new releases while running in the system tray. Once a release is downloaded and scheduled, its version is remembered so subsequent ticks do not re-download it, and PresentMon shutdown cleanup is guarded by a lock timeout so a hung native call can never block updates from applying on exit.

---

## ⚡ Performance Architecture

To maintain negligible CPU and memory overhead, Clockwork isolates telemetry into discrete stages and parks idle loops:

```text
┌────────────────────────────────────────────────────────┐
│               Detection Loop (500 ms)                  │
│   Finds active game, manages session, sleeps if idle   │
└──────────────────────────┬─────────────────────────────┘
                           │
           ┌───────────────┴───────────────┐
           ▼                               ▼
┌─────────────────────────────┐ ┌────────────────────────┐
│   Telemetry Loop (100 ms)   │ │  Fast Loop (16 ms)     │
│  Polled GPU/CPU telemetry,  │ │  ETW frame events &    │
│  system RAM, session timer  │ │  latency (100 ms UI)   │
└──────────────┬──────────────┘ └──────────┬─────────────┘
               │                           │
               └──────────────┬────────────┘
                              ▼
               ┌─────────────────────────────┐
               │    Overlay UI Dispatcher    │
               │   Updates changed values    │
               └─────────────────────────────┘
```

* **Selective PresentMon Queries**: Only statistics enabled in settings (plus lightweight adapter utilization when GPU stats are active) are registered in dynamic and frame query elements.
* **True Idle Hibernation**: All three background loops hibernate on semaphores when inactive—the detection loop sleeps when all statistics are turned off or when the overlay is hidden with no tracked game, the telemetry loop parks when no game is tracked, and the fast frame loop runs only while the overlay is visible on screen.
* **Allocation & UI Optimizations**: `EnumWindows` window enumeration utilizes pre-cached delegates and unmanaged pointer contexts. Frame aggregation uses stack-allocated and reusable spans. While the overlay is hidden, string formatting and UI dispatcher posts are skipped entirely.
* **Change Deduplication**: The `StatSlot` pipeline formats and posts values to the UI thread only when the rounded, visible text representation actually changes.
* **Bounded Diagnostics**: Unhandled exceptions across `AppDomain`, `TaskScheduler`, and the Avalonia UI `Dispatcher` are logged to `%LocalAppData%\Clockwork\telemetry.log`, which automatically rotates to `telemetry.log.old` at 1 MB.

---

## 📁 Project Structure

```text
Clockwork/
├── .github/
│   └── workflows/              # CI and Velopack Release workflows
├── Assets/
│   └── Icons/                  # Application icons & metric SVGs
├── Controls/
│   └── AspectRatioBorder.cs    # Aspect-ratio preview control
├── Converters/
│   └── SvgAssetValueConverter.cs # Lightweight cached SVG-to-DrawingImage converter
├── Overlay/
│   ├── IgnoredApplications.cs  # Built-in and user process ignore blacklist
│   ├── OverlayController.cs    # Orchestrator for loops, detection, & telemetry
│   ├── OverlayStatFormatter.cs # Invariant string formatting & StatSlot change detection
│   ├── OverlayViewModel.cs     # Observable line collection
│   ├── OverlayWindow.axaml     # Topmost click-through overlay window
│   ├── Stats.cs                # StatRegistry, StatSet (bitmask), and stat definitions
│   └── Win32.cs                # P/Invoke definitions, monitor DPI helpers, & GameDetector
├── Services/
│   ├── AppUpdateService.cs     # Velopack background updater with periodic checks
│   ├── ChecksumParser.cs       # SHA-256 release checksum extraction
│   ├── FrameStatistics.cs      # Ring buffer for FPS & percentile low calculations
│   ├── MsiSignatureVerifier.cs # WinVerifyTrust & Authenticode MSI digital signature validation
│   ├── PresentMonMonitor.cs    # Native PresentMon API v3 client with aggregation
│   ├── PresentMonNative.cs     # C ABI bindings for PresentMonAPI2.dll
│   ├── PresentMonUpdateService.cs # Hardened PresentMon installer/updater
│   ├── SettingsService.cs      # JSON settings persistence with backup & path injection
│   ├── SingleInstance.cs       # Mutex and window activation handler
│   ├── StartupMessageBox.cs    # Dialog prompt for dependency status
│   └── TelemetryDiagnostics.cs # Local diagnostic & unhandled-exception logging with rotation
├── tests/
│   └── Clockwork.Tests/        # Unit test suite (xUnit)
│       ├── ChecksumParserTests.cs
│       ├── FrameStatisticsTests.cs
│       ├── OverlayFormattingTests.cs
│       ├── PresentMonNativeTests.cs
│       ├── SettingsServiceTests.cs
│       ├── StatRegistryTests.cs
│       └── WindowGeometryTests.cs
├── ViewModels/                 # MVVM view models for MainWindow
├── Views/                      # MainWindow XAML and custom chrome implementation
├── App.axaml                   # Application styles & system tray menu
├── Clockwork.csproj            # .NET 10 project definition
├── Clockwork.slnx              # Solution file
└── Program.cs                  # Entry point & Velopack launcher
```

---

## 🔧 Building & Testing

### Prerequisites

* Windows 10/11 x64
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Build

```shell
dotnet build Clockwork.slnx -c Release
```

### Run Tests

```shell
dotnet test Clockwork.slnx -c Release
```

### Run Application

```shell
dotnet run --project Clockwork.csproj -c Release
```

---

## 📄 License

Clockwork is licensed under the [MIT License](LICENSE).