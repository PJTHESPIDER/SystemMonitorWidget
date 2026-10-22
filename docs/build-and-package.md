# Building, testing, packaging and installing SystemMonitor

This document is the reproducible path from a clean checkout to an
installed widget. Every command below was actually run on the machine this
was written on; where a step could not be verified there, it says so
explicitly under [Windows-only verification still pending](#windows-only-verification-still-pending).

## Prerequisites

| Requirement | Version used | Notes |
|---|---|---|
| .NET SDK | 8.0 (10.0.402 also works) | `dotnet --version` |
| Windows SDK | 10.0.26100.0 | Provides `makeappx.exe` and `signtool.exe` |
| Windows 11 | 22H2 (build 22000) or later | The widget extension requires 22000 |

The Windows App SDK (2.5.1), the Widgets projection (2.0.5) and the
LibreHardwareMonitorLib sensor backend (0.9.6) are NuGet package
references. They restore automatically; nothing is vendored into the
repository.

## 1. Clean checkout

```powershell
git clone <repository-url> SystemMonitor
cd SystemMonitor
git submodule update --init --recursive   # if the repository uses submodules
dotnet restore
```

The repository deliberately does **not** contain any build output. The
`build/package-root/`, `build/publish-temp/` and `build/msix-output/`
directories, the `.msix` package, the signing `.pfx` and the runtime logs
are all generated and are excluded by `.gitignore`. Everything the
packaging script needs is created on demand.

## 2. Build

The project targets `net8.0-windows10.0.22000.0` and uses the Windows App
SDK in self-contained mode. It must be built for a specific architecture:
the self-contained Windows App SDK requires an explicit one, and the
default `AnyCPU` platform fails with *"WindowsAppSDKSelfContained requires
a supported Windows architecture."*

```powershell
# x64 (the architecture the package is normally built for)
dotnet build SystemMonitor/SystemMonitor.csproj -c Release -p:Platform=x64

# x86
dotnet build SystemMonitor/SystemMonitor.csproj -c Release -p:Platform=x86

# ARM64
dotnet build SystemMonitor/SystemMonitor.csproj -c Release -p:Platform=ARM64
```

All three were run and all three succeeded with 0 warnings and 0 errors.

## 3. Test

```powershell
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj
```

Result on the current checkout:

```
Passed!  - Failed: 0, Passed: 415, Skipped: 0, Total: 415, Duration: 6 s
```

The suite includes the update-pipeline lifecycle regression tests, which
guard the activation/deactivation contract described in
`docs/refactor-plan.md`. Those tests are not optional: any change to
`Services/UpdatePipeline.cs` must leave them passing.

The test project links the pure, hardware-independent source files
directly. It does **not** reference the Windows App SDK and does not open
any hardware, so it runs on a build agent.

## 4. Package

```powershell
# The password is read from the environment. It is never stored in the
# repository and never has a default value.
$env:SYSTEMMONITOR_CERT_PASSWORD = '<your password>'

./build/Create-TestMsix.ps1 -Runtime win-x64
```

If `SYSTEMMONITOR_CERT_PASSWORD` is not set, the script prompts for the
password with `Read-Host -AsSecureString`, so the input is not echoed and
does not appear in the shell history.

The script:

1. publishes the project for the requested runtime,
2. assembles the package root from the published output,
3. rewrites the manifest's identity and architecture through the XML DOM
   (not with regular expressions over the raw text),
4. packs the root with `makeappx pack`,
5. signs the package with `signtool`.

The outputs land in `build/msix-output/`:

| File | Contents |
|---|---|
| `SystemMonitor.msix` | The signed package |
| `testcert.pfx` | The signing certificate **with its private key** |
| `testcert.cer` | The public half of that certificate |

### The manifest rewrite

`build/Create-TestMsix.ps1` edits `SystemMonitor/SystemMonitor/Package.appxmanifest`
through `System.Xml.XmlDocument` and sets only three attributes on the
`Identity` element: `Name`, `Publisher` and `ProcessorArchitecture`. It
also resolves the `$targetnametoken$` executable token on the element
that carries it.

Everything else is left untouched, deliberately:

- the package identity stays `com.test.systemmonitor` / `CN=Developer`,
- the COM class id stays `f72c45b3-9fa6-4155-82de-ab4ccf67cdb8`, in both
  the `com:Class` registration and the `uap3:CreateInstance` activation,
- the widget extension id stays `SystemMonitorWidget`,
- the `runFullTrust` capability stays.

### Architecture

`-Runtime` accepts `win-x64`, `win-x86` and `win-arm64`, and the manifest's
`ProcessorArchitecture` is set to match. Any other value is a hard error
rather than a silent fallback.

### Output path safety

The script deletes and recreates `build/publish-temp/`,
`build/package-root/` and `build/msix-output/`. Before any recursive
deletion, each path is resolved to an absolute path and checked to be
strictly inside `build/`, to be a directory rather than a file, and not to
be a symlink or junction. A path that fails any of those checks aborts
the run.

## 5. Install

```powershell
# Trust the test signer (only needed for a self-signed test package)
./build/install-cert.ps1

# Install the package
./build/install-msix.ps1
```

`install-cert.ps1` exports the public certificate from the PFX and adds it
to `CurrentUser\TrustedPeople`. It does not need the private key, and it
never writes one out. `install-msix.ps1` removes any existing
`com.test.systemmonitor` package and installs the new one.

Both scripts derive their paths from their own location, so they work from
any checkout path.

To remove the package:

```powershell
Get-AppxPackage -Name "com.test.systemmonitor" | Remove-AppxPackage
```

## 6. Diagnostics

Diagnostics are off by default. The shipping build previously produced no
diagnostics at all, because every log site was a `Debug.WriteLine`, which
the compiler removes from a Release build.

### Enabling the log

```powershell
$env:SYSTEMMONITOR_DIAGNOSTICS = "1"
```

This writes a rate-limited log to
`%LOCALAPPDATA%\SystemMonitor\systemmonitor.log`. Each line records the
backend, the operation, the status and the elapsed time:

```
2026-10-21 06:17:35.234 DEBUG [collector/cpu-load] ok elapsed=0.1ms
2026-10-21 06:17:35.386 ERROR [collector/acpi-thermal-zone] failed elapsed=110.2ms | ManagementException: Access denied
2026-10-21 06:17:35.387 WARNING [sensor/acpi-thermal-zone] unavailable | read failed
2026-10-21 06:17:36.230 INFO [LHM/read] ok elapsed=170.2ms | devices=3
```

Opting in lowers the level threshold to `Debug`, so the successful
collection events are recorded alongside the failures. Without that, the
log would be empty on a healthy machine and there would be no way to
tell "nothing failed" from "nothing ran".

Repeated identical events are collapsed: the first few are written in
full, and then a summary line reports how many were suppressed and how
many occurred in total. A sensor that is genuinely unavailable is
explained once, with a reason, and is not reported again until it
recovers — so a permanently missing sensor does not alarm the user every
five seconds.

The log deliberately does **not** contain serial numbers, machine
identifiers, device names, instance ids, PCI device keys, widget ids,
network interface identities, the account name or file paths. Exception
messages are scrubbed of paths before they are written, and only the
exception *type* is kept — the type is what makes a failure actionable,
and the message is where the identifiers live.

This was verified against a real run: the log of a live collection
contains none of the account name, the CPU model, the LibreHardwareMonitor
device identifiers, the PCI vendor keys or the log's own path, while the
opt-in inventory contains all of them.

### Exporting an inventory

```powershell
# Through the widget host
SystemMonitor.exe --export-diagnostics C:\temp\inventory.txt
SystemMonitor.exe --export-diagnostics=C:\temp\inventory.txt
```

The export is a deliberate, per-request opt-in. It lists every device,
its identity and each sensor's metric, status, value and unit. **This
file describes one machine in detail and may contain device identifiers.**
It is not written unless it is asked for, and it is not written to the
log directory.

## 7. What is not in the repository

| Path | Why |
|---|---|
| `build/package-root/` | Generated by the packaging script |
| `build/publish-temp/` | Generated by `dotnet publish` |
| `build/msix-output/` | Generated by the packaging script |
| `*.msix`, `*.appx`, `*.appxbundle` | Build artifacts |
| `*.pfx`, `*.p12`, `*.snk` | Signing material — **never commit a private key** |
| `*.log` | Runtime output, describes the author's machine |
| `systemmonitor-inventory.txt` | The opt-in inventory, describes the author's machine |
| `*.pubxml.user` | Per-user publish state |

The three `.ps1` files in `build/` are the only tracked files in that
directory, and they are the only packaging scripts.

## 7a. Driving the live Widgets board

The `build/` directory also holds the helper scripts used to drive the
real Widgets board for the live-host validation. They are read-only with
respect to the widget provider: they open the card's own menus, pick a
size, read the rendered text, and capture images. They do not install,
remove or re-register anything.

| Script | What it does |
|---|---|
| `widget-board-probe.ps1` | Reads the card's rendered text; with `-Size`, changes the size through the host's own "More options" menu and verifies the change against the card's rectangle |
| `widget-card-measure.ps1` | Measures one card: the geometry of every rendered text run, and a screenshot cropped to the card |
| `widget-card-shot.ps1` | Captures a screenshot of one card, cropped to its rectangle |
| `widget-card-add.ps1` | Adds the card from the host's "Add widgets" picker |
| `widget-card-remove.ps1` | Removes the card through the card's own "More options" → "Remove" |
| `widget-card-menu.ps1` | Lists what the card's "More options" menu offers |
| `widget-card-menu-click.ps1` | Clicks one item of that menu by name |
| `widget-card-deactivate.ps1` | Measures the provider process's CPU rate, which is the observable that distinguishes "still polling" from "stopped" |
| `widget-provider-list.ps1` | Lists the providers the host's picker shows |
| `widget-desktop-dump.ps1` | Dumps the UI Automation tree of the widgets windows, for finding elements that live in windows of their own |

The board is opened with `Start-Process "ms-widgetboard:"`.

**Note on the picker.** The host's picker is a window of its own, in a
process of its own. It is found by searching the whole desktop for its
title, not by looking inside the board window.

**Note on the menu items.** The host's menu items do not expose an
invoke pattern, so they are clicked with synthetic mouse input at the
point UI Automation reports for them. The first click on a popup that
does not yet own the input focus only takes the focus, which is why
`widget-card-remove.ps1` retries.

## Windows-only verification still pending

The following was actually run on the machine this document was written
on, and passed:

- the packaging script end to end (publish → manifest rewrite → `makeappx
  pack` → `signtool sign`), producing a 67 MB signed package,
- `signtool verify /pa` on the produced package,
- `install-cert.ps1` (the exported `.cer` loads with `HasPrivateKey:
  False`, so only the public half was written out) and `install-msix.ps1`,
- `Add-AppxPackage` and `Get-AppxPackage` on the resulting package, which
  registers as `com.test.systemmonitor_1.0.0.1_x64__1b7q5sa4bwdpa` with
  both extensions present (the COM server and the widget
  `uap3:AppExtension`),
- the COM class id `f72c45b3-9fa6-4155-82de-ab4ccf67cdb8` and the widget
  activation `ClassId`, in the source manifest, in the packed manifest
  and in the installed manifest,
- every LibreHardwareMonitorLib dependency in the published output, in the
  package and in the installed package,
- the `--export-diagnostics` switch against the installed binary, and the
  opt-in log against a real collection, including the check that the log
  contains none of the account name, the CPU model, the device
  identifiers or the PCI vendor keys,
- **the widget rendering and updating on the Widgets board.** The card
  was pinned from the host's own picker, rendered in all three sizes,
  resized, deactivated, reactivated, removed, re-added and duplicated on
  the live board. The rendered text runs were measured with UI
  Automation and the cards were captured as images. The evidence is in
  `docs/hardware-validation.md` §2.6 and §2.9.

The following is still pending, because it needs hardware or an
interactive session that was not available:

- **The x86 and ARM64 packages install.** Both architectures build and
  publish cleanly, their output contains every sensor-backend
  dependency, and their manifests were rewritten to the matching
  `ProcessorArchitecture`, but only the x64 package was taken through the
  full install path.
- **The nvidia-smi fallback path** is exercised on NVIDIA hardware only.
  The parsing and reconciliation logic is covered by fixture tests.
- **AMD GPU paths.** No AMD machine was available; those paths are
  fixture-verified only.
- **Sensors requiring elevation.** The MSR-based CPU temperature and
  clock sensors generally need elevated privileges. On the machine this
  was written on they reported `Failed`, and the log showed the reason
  (`Access denied`) without alarming on every cycle — which is the
  intended behaviour, but it does mean the elevated path is untested.
- **The self-signed certificate is not a real publisher certificate.**
  The identity `CN=Developer` and the `com.test.systemmonitor` package
  identity are development placeholders and must be replaced before any
  real distribution.

## A note on the private key

`build/msix-output/testcert.pfx` contains a private key. It is generated
by `build/Create-TestMsix.ps1`, it is excluded by `.gitignore`, and it has
never been committed — `git log --all -- build/msix-output/testcert.pfx`
returns nothing. It is reported here without its contents.

It should nonetheless be treated as a local development secret. If a
copy of it ever reaches a shared location, the safe response is to
remove the copy and let the packaging script issue a fresh certificate;
the certificate is a self-signed test signer, so rotating it costs
nothing except re-running the install step. Nothing in this repository
rotates or deletes it automatically.
