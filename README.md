# ⚙️ Clockwork

**Clockwork** is a lightweight Windows system-monitoring application built with **C#**, **.NET 10**, and **Avalonia UI**. It provides an in-game telemetry overlay focused on frame rate, frame time, latency, CPU, GPU, RAM, and session metrics, while keeping the application simple, unobtrusive, and conscious of anti-cheat integrity.

The name **Clockwork** is inspired by *...Like Clockwork* by **Queens of the Stone Age**.

---

## ✨ Highlights

* Clean, dark desktop interface with a restrained graphite visual system.
* Configurable in-game overlay with persistent position, scale, background, and opacity settings.
* Frame-rate monitoring including FPS, average FPS, 1% low FPS, 0.1% low FPS, and frame time.
* PresentMon API v3-backed GPU, frame-pacing, and latency telemetry.
* Workload-focused CPU telemetry avoiding kernel-level drivers (no PawnIO or custom kernel modules).
* RAM usage and private working-set memory monitoring.
* Session playtime and system clock information.
* Automatic detection of foreground fullscreen and borderless games, with background session retention during Alt-Tab. Windowed games are supported via an opt-in toggle.
* Three decoupled background loops:
* **Detection loop** (500 ms) for foreground tracking and window state.
* **Telemetry loop** (100 ms) for CPU, GPU, system RAM, and session timers.
* **Fast frame loop** (16 ms ETW drain, 100 ms UI publish) active only when high-frequency frame or latency metrics are enabled.


* Debounced local settings persistence in `%LocalAppData%\Clockwork`.
* System tray integration: closing the window hides Clockwork to the tray; shutdown is performed via the tray menu.
* Automatic PresentMon service detection, downloading, SHA-256 verification, and silent MSI installation.
* Silent background application updates powered by Velopack.
* Self-contained x64 Windows build.

---

## 🖥️ What Clockwork Does

Clockwork consists of two primary components:

1. **The Control Window** — configure active metrics, adjust overlay position/scale/styling via a live interactive preview, and toggle windowed-game tracking.
2. **The In-Game Overlay** — a hardware-accelerated, transparent, click-through (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`) overlay positioned over the game's client area.

By default, the overlay activates only over foreground games that cover their monitor (fullscreen exclusive or borderless windowed). Floating windowed games can be tracked by enabling **Include Windowed Games** in the Overlay settings.

---

## 📊 Available Statistics

Clockwork provides **41 configurable statistics** organized across six categories, with only **FPS** enabled by default:

### Performance

| Statistic | Description |
| --- | --- |
| **FPS** | Live frame rate calculated over the preceding 1-second window. |
| **Avg FPS** | Running average frame rate across the active session (idle gaps >2 s excluded). |
| **1% Low FPS** | Average frame rate of the slowest 1% of frames across the last 60 seconds. |
| **0.1% Low FPS** | Average frame rate of the slowest 0.1% of frames across the last 60 seconds. |
| **Frame Time** | Average recent frame time in milliseconds (1-second window). |
| **Dropped Frames** | Indicates whether frames were dropped (`Yes`/`No`). |
| **Presented FPS** | Presentation rate reported by PresentMon. |
| **Displayed FPS** | Displayed frame rate reported by PresentMon. |
| **Application FPS** | Application-level target/render frame rate reported by PresentMon. |

### CPU

| Statistic | Description |
| --- | --- |
| **CPU Usage** | Total CPU utilization percentage. |
| **CPU Busy** | CPU execution time per frame before waiting (ms). |
| **CPU Wait** | CPU wait time per frame (ms). |
| **CPU Frame Time** | Total CPU-side frame processing time (ms). |

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
| **GPU Busy** | GPU execution time per frame (ms). |
| **GPU Wait** | GPU idle/wait time per frame (ms). |
| **GPU Time** | Total GPU processing duration per frame (ms). |

### RAM

| Statistic | Description |
| --- | --- |
| **RAM Usage** | System physical memory used vs. total (`used / total GB`). |
| **RAM Usage (%)** | System memory utilization percentage. |
| **Process/Game RAM Usage** | Private working set of the tracked game process (GB). |

### Latency

| Statistic | Description |
| --- | --- |
| **GPU Latency** | Time between GPU work submission and completion (ms). |
| **Display Latency** | Time between presentation and display scanout (ms). |
| **Render/Present Latency** | Total latency from render start to present call (ms). |
| **Time Until Displayed** | Duration a presented frame waits until appearing on screen (ms). |
| **Between Presents** | Interval between consecutive present calls (ms). |
| **Between Display Changes** | Interval between actual display refreshes (ms). |
| **Input-to-Photon Latency** | End-to-end input-to-display latency across supported input events (ms). |

### More

| Statistic | Description |
| --- | --- |
| **System Time** | Current local clock time in `HH:mm`. |
| **Session Playtime** | Elapsed session duration in `HH:mm:ss`. |

---

## 🎯 Calculation Methodology

### Frame Rate & Frame Time

A ring buffer (`FrameStatistics`) holds up to **65,536 recent frame times** with high-resolution timestamps (`Stopwatch` ticks).

* **Live FPS**: Calculated as `1000.0 * frame_count / elapsed_ms` over the trailing 1,000 ms window.
* **Frame Time**: Arithmetic mean of all frame durations within the trailing 1,000 ms window.
* **Data Stale Timeout**: If no new frame arrives within 2,000 ms (e.g., loading screens or pause menus), calculations return `N/A` rather than holding an old value.

### 1% and 0.1% Lows

Lows are recalculated once per second across a **60-second rolling window**:

1. All frame durations from the last 60 seconds are copied into a buffer.
2. The samples are sorted in ascending order outside the telemetry lock.
3. The slowest 1% and 0.1% samples are sliced and averaged.
4. The average duration is converted to FPS: `1000.0 / average_slow_frame_time_ms`.

### Average FPS

The running session average tracks total active frames divided by total active time. Frame durations >= 2,000 ms are filtered out as idle gaps (such as Alt-Tab or loading screens) so they do not artificially depress the session average.

---

## 🎮 Game Detection & Session Tracking

Clockwork detects games without installing system hooks:

1. **Foreground Candidate Query**: Evaluates the foreground window using Win32 API calls (`GetForegroundWindow`, `GetWindowRect`, `GetClientRect`, `ClientToScreen`).
2. **Monitor Boundary Check**: Verifies whether the client rect covers the nearest display monitor (within a 4-pixel tolerance). If **Include Windowed Games** is checked, floating windows with a minimum client size of 320 x 240 qualify as well.
3. **Blacklist Filtering**: System components, browsers, chat tools, IDEs, media players, streaming tools, and other monitoring software are filtered out via executable name comparison.
4. **Process Tracking**: Reads the process ID, creation time, and private working set through `NtQuerySystemInformation(SystemProcessInformation)`.
5. **Session Persistence**: Sessions are indexed by `(Pid, CreateTime)`. When you Alt-Tab away from a game and return, session playtime and running statistics continue uninterrupted.

---

## 🛡️ Non-Invasive Anti-Cheat Design

Clockwork does not hook DirectX/Vulkan/OpenGL runtimes and does not inject code into running games:

* No DLL injection (`CreateRemoteThread`, `SetWindowsHookEx`, `AppInit_DLLs`).
* No process memory reading or writing (`OpenProcess` with memory access, `ReadProcessMemory`, `WriteProcessMemory`).
* No custom kernel-mode drivers.
* Overlay rendering is handled by an external Avalonia window positioned directly over the game's client coordinates using Win32 extended styles.
* Frame and GPU telemetry are gathered via ETW through the official Intel PresentMon Service.

---

## 📡 PresentMon & App Updates

### PresentMon Service Dependency

Clockwork utilizes the **PresentMon API v3** (`PresentMonAPI2.dll`). On startup, `PresentMonUpdateService` handles the dependency:

1. Scans standard installation paths (`%ProgramFiles%\Intel\PresentMonSharedService`, `%ProgramFiles%\Intel\PresentMon`, etc.).
2. Checks the official GitHub releases for `GameTechDev/PresentMon`.
3. If missing or older than the latest compatible release (v2.x line), the MSI installer is downloaded over HTTPS.
4. Validates the SHA-256 digest against the release checksum file or release digest.
5. Verifies the Authenticode signature on the MSI installer.
6. Executes `msiexec.exe /i <path> /qn /norestart` via an administrative prompt.
7. If offline, Clockwork continues seamlessly with the installed version or informs the user if PresentMon is missing.

### Application Updates (Velopack)

Clockwork checks for GitHub releases in the background via **Velopack**. When an update is ready, it downloads silently and applies upon closing Clockwork without interrupting active gaming sessions.

---

## ⚡ Performance Architecture

To maintain negligible CPU and memory overhead, Clockwork isolates telemetry into discrete stages:

```text
┌────────────────────────────────────────────────────────┐
│               Detection Loop (500 ms)                  │
│       Finds active game, manages session state         │
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

* **Selective PresentMon Queries**: Only statistics enabled in settings are registered in dynamic and frame query elements.
* **Fast Loop Hibernation**: If no latency or fast frame metrics are enabled, the 16 ms timer hibernates on a semaphore to eliminate unnecessary polling.
* **Change Deduplication**: The `StatSlot` pipeline formats and posts values to the UI thread only when the visible text representation actually changes.

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
│   ├── IgnoredApplications.cs  # Built-in process ignore blacklist
│   ├── OverlayController.cs    # Orchestrator for loops, detection, & telemetry
│   ├── OverlayStatFormatter.cs # Invariant string formatting & StatSlot change detection
│   ├── OverlayViewModel.cs     # Observable line collection
│   ├── OverlayWindow.axaml     # Topmost click-through overlay window
│   ├── Stats.cs                # StatRegistry, StatSet (bitmask), and stat definitions
│   └── Win32.cs                # P/Invoke definitions & GameDetector
├── Services/
│   ├── AppUpdateService.cs     # Velopack background updater
│   ├── ChecksumParser.cs       # SHA-256 release checksum extraction
│   ├── FrameStatistics.cs      # Ring buffer for FPS & percentile low calculations
│   ├── MsiSignatureVerifier.cs # Authenticode MSI digital signature validation
│   ├── PresentMonMonitor.cs    # Native PresentMon API v3 client
│   ├── PresentMonNative.cs     # C ABI bindings for PresentMonAPI2.dll
│   ├── PresentMonUpdateService.cs # Automated PresentMon installer/updater
│   ├── SettingsService.cs      # JSON settings persistence
│   ├── SingleInstance.cs       # Mutex and window activation handler
│   ├── StartupMessageBox.cs    # Dialog prompt for dependency status
│   └── TelemetryDiagnostics.cs # Local diagnostic logging
├── tests/
│   └── Clockwork.Tests/        # Unit test suite (xUnit)
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

Clockwork is licensed under the [MIT License](https://www.google.com/search?q=LICENSE).