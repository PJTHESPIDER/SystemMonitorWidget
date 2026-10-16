# SystemMonitor

A Windows 11 widget that puts live system metrics on your Widgets board.

Status: Active development. This project is a prototype and not yet an official release. The packaging identity and signing setup are still placeholders for development use.

# Features
CPU — utilization and clock speed

GPU — best-effort utilization, temperature, and VRAM readings; availability varies by hardware (temperature and VRAM try nvidia-smi first, then WMI; utilization uses WMI performance counters)

Memory — RAM usage

Disk — system drive utilization

Network — upload/download throughput

Temperatures — CPU and GPU temperature where sensors are exposed to Windows