# Winnow

A Windows 11 debloater, privacy switchboard and task manager in one native app. It lets you turn off Microsoft's tracking, remove what you don't want, clean the drives, install the apps you do want, and see what the PC is doing.

The task manager side is modelled on [TMOG](https://tmog.org) (Task Manager OG), Dave Plummer's modern take on the Task Manager he originally wrote. Winnow borrows TMOG's layout and its main rule, **measured, never invented**. A reading Winnow can't take shows as "—", and a graph leaves a gap. It never shows a zero that wasn't measured. Estimates are labelled "est.".

Windows 11 and later only. C# / WPF on .NET 10, built into one self-contained `Winnow.exe`.

## Which file do I use?

| File | What it's for |
|---|---|
| `build-exe.bat` | **Windows.** Double-click to build `dist\Winnow.exe`. It fetches a private copy of the .NET 10 SDK into `.dotnet\` the first time if the PC has none (no admin needed, nothing installed system-wide), builds, then runs the exe's self-test. `build-exe.bat arm64` builds for Snapdragon PCs. |
| `dist\Winnow.exe` | The app. Copy it anywhere and double-click. It asks for administrator rights once (UAC); say no and it runs in a limited, read-mostly mode. |
| `.github/workflows/build.yml` | Every push is built on a clean Windows machine with the same `build-exe.bat`. Its self-test runs, then a UI test (`Winnow.exe uitest`) opens every page and fails the build on any runtime fault. The exe is attached to each run as a download, and the log and screenshots are pushed to the `ci-log` branch. |

There is no Mac build. Winnow only does Windows things, so this is a deliberate exception to the usual Win + Mac rule.

## Pages

**Monitor**
- **Summary**: a one-line verdict on what is slowing the PC right now and which process is responsible. Shows dials for CPU, memory, disk, GPU and temperatures, the top processes for each subsystem, energy use, and a privacy status card.
- **Performance**: CPU (overall or per logical processor, with P-cores and E-cores coloured differently on hybrid CPUs), memory composition, each disk, each network adapter and the GPU, plus a **Thermals** table of every sensor on the board.
- **Processes**: a live process list or tree. Columns cover CPU, memory, disk, network, GPU and estimated power per process. Right-click to end, end tree, suspend, resume, set priority or open the file location. Every action checks the process's start time first, so a PID that Windows has recycled can never point the action at the wrong program. svchost.exe lists the services it hosts.
- **Connections**: every TCP/UDP connection with its process. It shows the host name this PC actually looked up for that IP, and bytes in and out. Microsoft telemetry and ad endpoints are flagged, and you can block a program's outbound traffic in one click.
- **Flight Recorder**: the last hour (configurable) kept in memory. Scrub back to "the slow minute", jump to peaks, save it as a `.winnowtrace` file, reopen it later, or export CSV.

**Manage**
- **Startup apps**: Run keys, Startup folders and logon/boot scheduled tasks. Enable and disable work exactly as Task Manager does it (the StartupApproved values).
- **Services**: start, stop, restart, and set the start type. Telemetry services are tagged. Services Windows can't run without can't be stopped or disabled here.
- **Installed Apps**: uninstall Win32 programs and Store apps, including provisioned copies so they don't come back for new users. There's a curated **Bloatware** list, and a **Leftovers** scan for folders and registry keys left behind by software that's gone. Removed folders go to the Recycle Bin, and registry keys are exported to a `.reg` file before deletion.
- **Disk Space**: measured clean-up (temp, Windows Update cache, Delivery Optimisation, thumbnails, error reports, crash dumps, Recycle Bin, browser and shader caches, Windows.old, component store) and a WizTree-style folder analyser.
- **Install**: a Ninite-style tick list of about 140 popular apps installed silently with winget, plus "upgrade all". Add your own apps with **Customise list…**, which writes `winnow-catalogue.json` beside the exe for you to edit.

**Privacy**
- 47 switches for telemetry, advertising, suggestions, Bing in Start, Copilot, Recall, Click to Do, activity history, Edge's data collection, Delivery Optimisation peering and more. Each is checked against Microsoft's own policy documentation, and the source is noted in the code.
- **Light / Recommended / Aggressive** presets. Each switch shows whether it's currently applied on this PC.
- A System Restore point is created before the first change in a session. You can switch that off in Settings.
- **History** lists every change Winnow has made on any page. Each change can be undone on its own, back to the exact value it replaced.

**System**: System Info (Secure Boot, TPM, VBS/HVCI, BitLocker, Defender, hardware), Users, Drivers (unsigned drivers and known-vulnerable ring-0 helpers are flagged), and Settings.

## What Winnow deliberately does not touch

It doesn't touch Windows Update, Microsoft Defender, SmartScreen, UAC or the firewall's on/off state. Debloaters that switch those off are how PCs get broken or compromised. The firewall is only used to *add* block rules, and each one can be undone.

## Honest limits

- **Telemetry level.** On Windows 11 **Home and Pro**, Windows won't let diagnostic data go below "Required"; the "Security" level is only honoured on Enterprise and Education. Winnow sets the policy anyway, and also disables the services and scheduled tasks that do the collecting. The Privacy page shows which edition you're on.
- **Temperatures and power.** These need ring-0 access. Winnow uses LibreHardwareMonitor with the **PawnIO** driver, which is Microsoft-signed, works with Memory Integrity, and isn't flagged by Defender. It does not use the old WinRing0 driver that Defender quarantines. Winnow installs PawnIO once (via winget, or from its official release) when it first runs as administrator. Without it, CPU temperatures and power show "—". Settings › Hardware sensors has a button that reads **Install**, **Repair** (registered but its driver file is gone, e.g. deleted by hand - the stale entry is cleared first) or **Reinstall** to match PawnIO's state.
- **Per-process network** comes from Windows' own event tracing (the Kernel-Network provider), so it needs administrator rights.
- **Per-process power** is an estimate: the process's share of CPU time × CPU package power, plus its share of GPU time × GPU power. It's labelled "est." everywhere.
- **Updates.** Feature updates sometimes put Microsoft's defaults back. Reopen the Privacy page after a big update. It re-checks every switch and shows anything that has been reset.

## Files Winnow keeps beside the exe

`winnow-settings.json` (settings), `winnow-journal.json` (the undo history — keep it), `winnow-log.txt`, `winnow-crash.log`, and `Winnow-removed-*` folders holding anything moved out of Startup or exported from the registry. Anything you save goes to **Downloads\Winnow** unless you choose another folder in Settings.

## Licence

MIT No Attribution. Uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0), [PawnIO](https://pawnio.eu) (installed separately, not bundled) and [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) (MIT).
