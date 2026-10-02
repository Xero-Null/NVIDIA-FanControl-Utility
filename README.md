# NVIDIA Fan Control Utility

*** This utility was created for use with server workstation graphic cards, like my NVIDIA Tesla V100 32GB AI Rig has with passive cooling to control the loud 8kRPM Artic fan speed using the Fan Control Windows application. ***

A clean, dark-themed Windows GUI (C# / WinForms, .NET 8) that monitors NVIDIA GPUs.
It runs `nvidia-smi` through **PowerShell** on a 5-second timer, parses the console
output, and shows each GPU in its own card with:

- **GPU usage** as a 0–100% slider (NVIDIA green)
- **Memory usage** as a 0–100% slider (blue) with used / total GiB
- **Temperature** in °C, color-coded (green < 65°, yellow < 80°, red ≥ 80°)
- **Power draw** in watts
- **Fan speed** in %
- **PCIe link** (generation and lane width, e.g. `Gen 4 · x16`)
- **Graphics clock** (current / max, in MHz)
- GPU **name** and **driver version** context

If `nvidia-smi` can't be read (missing driver, not installed, no devices), the app
shows a clear error card instead of failing silently, and you can retry with the
**Refresh** button.

## Requirements

- Windows 10/11
- An NVIDIA GPU with a working driver (verify by typing `nvidia-smi` in a Command Prompt)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
  (or: `winget install Microsoft.DotNet.SDK.8`)

## Quick start

```bat
:: from this folder
dotnet run --project NVIDIAFanControlUtility\NVIDIAFanControlUtility.csproj
```

Or just double-click **build.bat**.

## Building

```bat
dotnet build NVIDIAFanControlUtility\NVIDIAFanControlUtility.csproj -c Release
```

## Packaging a single .exe (optional)

Double-click **publish.bat**, or run:

```bat
dotnet publish NVIDIAFanControlUtility\NVIDIAFanControlUtility.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The portable executable is created at:

```
NVIDIAFanControlUtility\bin\Release\net8.0-windows\win-x64\publish\NVIDIAFanControlUtility.exe
```

It is self-contained (no .NET runtime needed on the target machine).

## How it works

Every 5 seconds (or when you press **Refresh**), the app launches:

```
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ^
  "& nvidia-smi --query-gpu=index,name,driver_version,temperature.gpu,utilization.gpu,utilization.memory,memory.total,memory.used,power.draw,power.limit,fan.speed,pcie.link.gen.current,pcie.link.width.current,clocks.current.graphics,clocks.max.graphics --format=csv,noheader,nounits"
```

The CSV rows are parsed column-by-column; `[N/A]` values (fan on some cards,
power on some vGPUs, etc.) are displayed as `--`. The query runs on a background
thread so the UI never freezes.

## Fan Control integration (Options tab)

If you use [Fan Control](https://github.com/FCitibrix/FanControl), NVIDIA Fan Control Utility can
switch its profiles automatically as your GPUs heat up:

1. On the **Options** tab, check **Enable Fan Control profile switching**.
2. Point at `FanControl.exe` (default `C:\FanControlFolder\FanControl.exe`; use
   **Browse…** to change it).
3. Add profile rows: `at <°C> load <profile name>`, coolest trigger first. The
   name is your Fan Control config — `.json` is appended automatically.

How it decides:

- The **hottest GPU** across all cards drives the trigger.
- The active profile is the hottest one whose trigger is at or below the current
  temperature; when the temperature is below every trigger, the first (coolest)
  profile is used.
- Moving **up** the ladder is immediate; moving **down** requires the GPU to cool
  at least 3° below the trigger of the profile being left (anti-flap, when the
  temperature hovers around a boundary).
- Only when the selected profile actually changes does NVIDIA Fan Control Utility run
  `FanControl.exe -c "<name>.json"`, with the exe's own folder as the working
  directory (Fan Control requires that). If Fan Control isn't running, this
  starts it; if it is, the running instance just switches config — Fan Control's
  documented single-instance behavior.

A live status line under the Options tab shows which profile is active and flags
launch failures (full details go to the log). The options persist across restarts
in `%TEMP%\nvidia_fan_control_utility_settings.json`, next to the error log.

## Project layout

```
NVIDIAFanControlUtility\
├── NVIDIAFanControlUtility.csproj       # .NET 8 WinForms project
├── Program.cs              # entry point
├── Theme.cs                # all dark-theme colors in one place
├── AppLogger.cs            # file logger (%TEMP%\nvidia_fan_control_utility_errors.log)
├── MainForm.cs             # window, tabs, options page, refresh timer
├── Models\
│   ├── GpuSnapshot.cs      # one parsed GPU reading
│   └── FanControlSettings.cs # Fan Control profile ladder (model)
├── Services\
│   ├── NvSmiService.cs     # PowerShell invocation + CSV parser
│   ├── FanControlService.cs# profile ladder + hysteresis + launching Fan Control
│   └── SettingsStore.cs    # JSON settings persistence
└── Controls\
    ├── PercentSlider.cs    # custom read-only 0–100% slider
    ├── GpuPanel.cs         # the per-GPU card
    ├── TabStrip.cs         # dark, owner-drawn tab strip
    └── ErrorCard.cs        # shown when nvidia-smi fails
```

## Troubleshooting

- **"nvidia-smi was not found"** — the NVIDIA driver isn't installed or isn't on
  the PATH. Install the driver from nvidia.com and confirm `nvidia-smi` works in
  a normal Command Prompt.
- **"No devices were found"** — nvidia-smi is installed but sees no GPU (common on
  VMs without GPU passthrough).
- **Wrong / stale values** — some virtualized GPUs report `[N/A]` for fan or
  power; those show as `--`.
- **Diagnosing a failure** — the error card shows the exact message. You can also
  run this in a Command Prompt to see exactly what the app sees:

  ```bat
  powershell -NoProfile -Command "nvidia-smi --query-gpu=index,name --format=csv,noheader,nounits"
  ```

  If that prints GPU names, the app should work; if it errors, fix the driver
  first. Hover the status bar text for the full error message.
