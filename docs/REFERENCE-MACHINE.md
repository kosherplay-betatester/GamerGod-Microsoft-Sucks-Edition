# Reference Machine

GamerGod's primary development and validation target. Every measurement in
`docs/MEASUREMENTS.md` is taken here unless stated otherwise. Captured 2026-07-29.

## Specification

| Component | Detail |
|---|---|
| **CPU** | AMD Ryzen 9 7950X3D — 16C/32T, AM5, Family 25 Model 97 Stepping 2, 4201 MHz base |
| **L3 topology** | 128 MB total across two CCDs — **96 MB (V-Cache) + 32 MB**. WMI reports only the 131072 KB aggregate; per-domain split requires `GetLogicalProcessorInformationEx(RelationCache)` |
| **L2** | 16 MB total (1 MB × 16 cores) |
| **Motherboard** | ASUSTeK PRIME X670-P WIFI, BIOS 3854 (2026-03-04) — recent AGESA, relevant to X3D core parking behaviour |
| **RAM** | 64 GB Corsair CMH64GX5M2D6000Z40, 2×32 GB, DDR5-6000 (EXPO active: JEDEC 4800 → configured 6000) |
| **dGPU** | NVIDIA GeForce RTX 5070 Ti, driver 32.0.16.1088 (2026-07-22) |
| **iGPU** | AMD Radeon Graphics (7950X3D integrated RDNA2), driver 32.0.21045.1000 (2026-07-23) |
| **Storage** | MSI M480 PRO 1 TB NVMe (primary) + Seagate ST2000DM008 2 TB SATA HDD |
| **Network** | **Wi-Fi only** — Realtek 8852BE WiFi 6 PCIe, 573.5 Mbps link. No wired Ethernet |
| **Display** | MSI panel (`DISPLAY\MSI4BA9`), ~24" (53 × 29 cm) |
| **OS** | Windows 11 Pro 25H2, build 10.0.26200 |
| **Toolchain** | .NET SDK 10.0.302 + 9.0.119, git 2.45.1, pwsh 7, winget |

## Security posture

Relevant because Charter Article V forbids weakening any of it, and because
kernel anti-cheats gate on it.

| Feature | State | Consequence |
|---|---|---|
| Secure Boot | ✅ Enabled | Battlefield 6 (Javelin) and Black Ops 7 will launch |
| TPM 2.0 | ✅ Present, ready, enabled | Same |
| VBS | ✅ Running (status 2) | Hypervisor active — small measurable perf cost, **do not disable** |
| HVCI / Memory Integrity | ⚠️ Not running (`SecurityServicesRunning = 0`) | *Capability* is what Javelin checks, so BF6 is fine. Enabling it would cost a little performance and gain real security — user's call, GamerGod only reports |

## Installed software relevant to GamerGod

| Software | Role |
|---|---|
| **Battlefield 6** | Kernel anti-cheat (EA Javelin) — the Ambient-only compatibility test case |
| **RivaTuner Statistics Server 7.3.7** | Frame limiting via shared-memory API — the VRR-sweet-spot integration target |
| **MSI Afterburner 4.6.6** | On suspension denylist (holds fan curves) |
| **3DMark** | Deterministic repeatable workload for the A/B harness |
| **Steam** | Launcher-inheritance path for Contact routing |

## Environment hazards detected

This scan is itself a GamerGod feature (`EnvironmentHazardScan`). Everything below was
found automatically and is a real frametime or compatibility risk on this box.

| Hazard | Severity | Why it matters |
|---|---|---|
| **`Virtual Desktop Monitor` driver in `Error` state** | 🔴 High | A display adapter failing to initialise can cause DWM stalls and mode-set hitches. Broken display drivers are a top-tier stutter source. Should be repaired or removed. |
| **4 virtual display adapters** (Virtual Desktop, Parsec, + NVIDIA/AMD virtual outputs) | 🟡 Medium | Each participates in display enumeration and DWM composition. Multiple virtual outputs are a documented cause of VRR/G-Sync misbehaviour and mode-set latency. |
| **VMware installed + VBS/Hyper-V running** | 🟡 Medium | Two hypervisor stacks coexisting. Kernel anti-cheats are documented to conflict with third-party hypervisors — the most likely cause of a BF6 launch failure on this machine, and *not* something GamerGod would have caused. Worth knowing before blaming us. |
| **Wi-Fi only, no wired Ethernet** | 🟡 Medium (multiplayer) | For BF6-class multiplayer, a wired connection is worth more than every software lever in this project combined. GamerGod's network QoS helps with local contention; it cannot fix RF jitter. **This is the single highest-value upgrade available on this machine.** |
| **8 audio endpoints** incl. Steam Streaming, Virtual Desktop Audio, NVIDIA Virtual Audio | 🟢 Low | Extra endpoints cost little, but each virtual device adds an audio graph node. Worth pruning unused ones. |
| **Hybrid GPU (AMD iGPU + NVIDIA dGPU)** | 🟢 Low | Games must target the RTX 5070 Ti. GamerGod should verify per-app GPU preference rather than assume. |
| **Bluetooth audio (Shokz OpenFit 2+)** | 🟢 Info | BT audio is latency-sensitive; `bthserv`/`BTAGService` are on the hard service denylist. |
| **Tailscale tunnel adapter** | 🟢 Info | Virtual NIC. Excluded from interrupt-steering targets. |

## What this machine validates

- ✅ **Asymmetric dual-CCD** Performance Domain detection (96 MB vs 32 MB) — the flagship path
- ✅ **Hybrid GPU** vendor abstraction (NVAPI + ADLX on one box)
- ✅ **Kernel anti-cheat compatibility** (BF6/Javelin installed and playable)
- ✅ **Secure Boot + TPM + VBS on** — proves GamerGod delivers gains without touching security
- ✅ **Wi-Fi + virtual adapters** — a realistically messy environment, not a clean-room bench
- ❌ Does **not** validate: Intel hybrid P/E-core domains, single-CCD AMD, handhelds, wired NIC
  interrupt steering, Snapdragon. Those need CI hardware or community contributors.
