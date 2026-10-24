# SystemMonitor

A Windows 11 widget that puts live system metrics on your Widgets board.

Status: Active development. This project is a prototype and not yet an official release. The packaging identity and signing setup are still placeholders for development use.

# Building

See [docs/build-and-package.md](docs/build-and-package.md) for the build, test, package and install steps from a clean checkout, and for the opt-in diagnostics.

# Features

CPU — utilization, clock speed, and temperature where readings are available

GPU — best-effort utilization, temperature, and memory readings; availability varies by hardware. On multi-GPU systems, the widget chooses a primary device automatically and may show eligible secondary devices; there is no in-widget device picker.

Memory — RAM usage

Disk — system drive utilization

Network — upload/download throughput

Temperatures — CPU and GPU readings where sensors are available; unavailable values are shown as N/A
