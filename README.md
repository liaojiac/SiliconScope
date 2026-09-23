# SiliconScope

English | [简体中文](README.zh-CN.md)

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4.svg)](#)
![.NET: 8.0](https://img.shields.io/badge/.NET-8.0-512BD4.svg)
![C#: latest](https://img.shields.io/badge/C%23-latest-239120.svg)

SiliconScope is a Windows desktop application that rates the **silicon quality** of AMD Ryzen CPUs from
their **steady-state, full-load V/F operating points**. It is built with C# / .NET 8 and WPF, reads sensors
through [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (PawnIO driver),
and can optionally read **per-core VID** from the HWiNFO shared memory. It runs fully offline — no data
ever leaves your machine.

> Current release: **V1.0.0** (first open-source release). The single source of truth for the version is
> [`UI/AppInfo.cs`](UI/AppInfo.cs).
> Real-hardware calibration baseline: Ryzen 7 7800X3D (Zen4 / SVI3 / Windows 10 22H2). Other models are
> derived from specifications and are clearly labeled as estimates in the UI.

---

## What it measures

After confirming that the CPU is **genuinely under sustained full load**, SiliconScope samples the steady-state
core voltage and clock for about 10 seconds, then **linearly normalizes** operating points from different
stress tools to a common *anchor frequency* using the V/F slope. The normalized voltage is compared against a
per-model reference voltage to produce a **0–150 silicon score** with a five-tier rating
(Supreme / Great / Average / Weak / Poor).

Clock frequency is used only for the full-load gate and V/F normalization — it is **not** part of the score —
so a heavier load (e.g. OCCT, lower frequency/voltage) and a lighter load (e.g. CPU-Z, higher frequency/voltage)
produce repeatable, comparable results.

This score is an **independent, reproducible** steady-state V/F metric. It is **not affiliated with, and does
not reproduce, any motherboard or chip vendor's closed-source internal score**.

## Features

- Environment self-check (administrator rights, LHM/PawnIO driver, sensor count, Core Isolation status —
  **you never need to disable any security feature**).
- Live monitoring plus a full-load stability gate: sampling **auto-starts after 60 seconds of sustained full
  load** (sliding-window ratio ≥ 90%), preventing premature, inaccurate runs.
- ~10-second multi-round sampling (500 ms × 20 rounds); runs without enough full-load coverage are rejected.
- Whole-die core voltage (VDDCR / SVI3) drives the main score; per-core VID passes a quality gate and is used
  for the inter-core uniformity / best-vs-worst distribution.
- Five-tier rating, V/F fit chart, per-core voltage bars, confidence level, and parameter provenance
  (model-calibrated / generation default / spec-derived).
- On-device history (saved locally; view/delete), plus JSON and Markdown report export.
- Optional multi-point V/F self-calibration (accepted only when R² ≥ 0.90).

## Download (no development environment required)

Get `SiliconScope_V1.0.0_win-x64.zip` from the [**Releases**](../../releases) page, **extract the entire
folder**, then right-click `SiliconScope.exe` → *Run as administrator*. It only reads sensors and does not
require disabling Core Isolation or your antivirus. To enable the per-core VID distribution, turn on HWiNFO
"Shared Memory Support" (optional) as described in the Debug handover document.

## Build & test

```bash
# Logic regression (no Windows / hardware required; Python 3)
python3 run_all.py

# Build (on Linux/macOS cross-targeting Windows, add EnableWindowsTargeting)
dotnet build SiliconScope.sln -c Release -p:EnableWindowsTargeting=true

# C# unit tests: Collector 40 tests, ScoringEngine 50 tests
dotnet run --project 采集层/tests/Collector.Tests.csproj -c Release -p:EnableWindowsTargeting=true
dotnet run --project 评分引擎/tests/ScoringEngine.Tests.csproj   -c Release
```

For publishing (**folder self-contained only — single-file is not supported**), post-processing, packaging,
and the release acceptance checklist, see `docs/交接文档/04_构建发布与测试手册.md` (Chinese handover doc).

## Repository layout

```
SiliconScope.sln
├─ 评分引擎/   ScoringEngine : platform-agnostic net8.0 library (V/F normalization, score, tiers,
│              3-level parameter resolution, self-calibration, export) + tests
├─ 采集层/     Collector      : net80-windows, real LHM acquisition, cross-vendor flexible naming,
│              HWiNFO shared memory, quality gates, logging, diagnostics + tests
├─ UI/         WPF (MVVM)     : startup project, produces SiliconScope.exe; wizard, results, history, settings
├─ docs/
│  ├─ 交接文档/                Chinese handover set (overview / development / technical / debug /
│  │                          build-release / limitations / source map / parameters / archive index)
│  ├─ archive/                 Pre-open-source internal design/iteration notes (archived)
│  └─ 版本与更名说明.md         Rename & version notes (CpuScore → SiliconScope; first public release V1.0.0)
└─ run_all.py  One-command logic regression (Collector + ScoringEngine + UI static checks)
```

## Documentation

The in-repo handover set under [`docs/交接文档/`](docs/交接文档) is written in Chinese and covers the full
development, technical, debug, and build/release workflow — start with `README_交接总览.md`. Rename and
version mapping is documented in [`docs/版本与更名说明.md`](docs/版本与更名说明.md).

## Third-party components

- .NET 8, WPF (MVVM), C#; Python 3 (logic regression / static checks).
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6 (MPL-2.0),
  including the embedded PawnIO driver.
- HWiNFO shared memory (SM2), accessed **read-only**. HWiNFO is a commercial product of its owner and is
  not bundled.

## License & disclaimer

Licensed under the **Apache License 2.0** — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).

Voltage/frequency readings come from third-party sensor libraries and HWiNFO; results are provided **for
silicon-quality reference only**, without any performance or stability guarantee. Currently only the
7800X3D is real-hardware calibrated; other models are estimates. WPF rendering, real sensors, and the HWiNFO
connection must be verified on a Windows machine — CI covers compilation, pure-logic unit tests, and static
checks. Cloud / leaderboard / account features are reserved interfaces; this release is fully offline.
