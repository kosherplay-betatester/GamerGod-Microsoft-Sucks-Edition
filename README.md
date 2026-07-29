<div align="center">

# GodMode
### *Microsoft Sucks Edition*
#### The Gamers' Redemption

**Give your game the machine you paid for. Give Windows it back when you're done.**

`Partition` · `Prove` · `Preserve` · `Protect`

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![Charter](https://img.shields.io/badge/Charter-binding-red.svg)](CHARTER.md)
[![Telemetry](https://img.shields.io/badge/telemetry-none%2C%20ever-brightgreen.svg)](CHARTER.md#iv-we-will-never-collect-telemetry)
[![Kernel driver](https://img.shields.io/badge/kernel%20driver-never-brightgreen.svg)](CHARTER.md#ii-we-will-never-ship-a-kernel-driver)

</div>

---

## The problem

You bought a 16-core CPU with 96 MB of stacked cache. Windows uses it to index your Steam
library, upload update deltas to strangers, and render a taskbar you cannot see because
you are in a fullscreen game.

Meanwhile every "optimization guide" on the internet tells you to disable VBS, turn off
Defender, and paste registry keys you cannot undo — and half of those tweaks are measured
by nobody, while some of them will stop Battlefield 6 from launching at all.

## What GodMode does

It makes the machine around your game quiet, and it can prove it.

- **Partitions your CPU.** Your game gets the fast cores. Everything else on the system
  gets evicted somewhere else. Works on AMD X3D, AMD multi-CCD, Intel P/E hybrid, and ARM
  — one abstraction, no special cases.
- **Demotes the rest of Windows** with Microsoft's own EcoQoS API, the same thing Task
  Manager's "Efficiency mode" uses. Reversible, universal, invisible to your game.
- **Tells you why you stuttered.** Not "try disabling fullscreen optimizations" — an
  actual answer: *"Your 340 ms hitch at 21:14 was Windows Search indexing your Steam
  library."* One click to stop it happening again.
- **Learns your machine.** Autotune A/B tests its own settings during real play and
  converges on what actually works for that game on your hardware.
- **Shows you an overlay that doesn't inject.** FPS, frametimes, 1% lows, per-core and
  per-domain CPU, GPU, temps — plus live stutter attribution while you're still in the
  match. Optional, off by default, instant toggle on `Ctrl+Shift+O` or `Guide+Y`.
- **Gives everything back.** Toggle off, crash, power cut, whatever — you get your
  desktop, your services, and your settings back exactly as they were.

### The overlay, specifically

RivaTuner and MangoHud inject a DLL into your game. GodMode doesn't have to — it reads
frame data from PresentMon's ETW stream out-of-process and draws in a separate
click-through window. Same HUD, zero contact with the game, works even on kernel
anti-cheat titles.

It also shows things no other overlay can: **which cores your game is actually on**,
game-domain vs background-domain CPU load side by side, and `⚠ hitch 340 ms — WSearch`
appearing live. Every other overlay tells you a number went bad. This one tells you what
did it.

## What GodMode will never do

Read [the Charter](CHARTER.md). It is binding, and Articles I–VI cannot be amended.

> **No kernel driver. No injection. No telemetry. No weakening your security.
> Nothing survives a reboot unless you said so. And it will never break your games.**

That last one outranks performance. A build that gains 20% and breaks one anti-cheat is
a failed build.

## Never breaks your games — architecturally

Every change GodMode can make is classified by one question: **can the game detect it,
even in principle?**

| **AMBIENT** | **CONTACT** |
|---|---|
| Changes only what *other* processes do | Touches the game process itself |
| Invisible to your game by construction | Observable |
| **Always allowed** | **Opt-in — and hard-blocked for kernel anti-cheat** |

For **Battlefield 6, Valorant, Fortnite, Apex, Rainbow Six** — GodMode runs 100% Ambient.
It never opens a handle to your game. Not "we're careful." It has no code path that can.

And most of the win is Ambient anyway: clearing every other process off your game's cores
is worth more than anything you could do *to* the game.

## Never claims a benefit it hasn't measured

Every toggle shows a real measured delta with a confidence interval, **from your
hardware** — or it shows `UNMEASURED` in grey.

A tweak whose confidence interval straddles zero is reported as *"no measurable effect,"*
even when the internet insists otherwise. Especially then.

Some honest examples from our own ledger:

| Lever | Reality |
|---|---|
| CPU domain partitioning (X3D) | The real one. Large on cache-sensitive titles. |
| Frame cap at the VRR sweet spot | The biggest latency win available, and it's free. |
| Killing `explorer.exe` | **~0% FPS.** It's a UX feature. We say so in the UI. |
| Network registry "tweaks" | Cargo cult. Not shipped. |
| Disabling VBS for FPS | **Stops Battlefield 6 from launching.** Not shipped — we warn you instead. |

## Status

🚧 **Early development.** The engine works and is verified on real hardware. It does not
change anything yet — every command that exists today only reads.

**125 tests**, including 500 randomised crash-recovery trials.

| Component | State |
|---|---|
| Charter, spec, architecture | ✅ Done |
| **Performance Domain detection** | ✅ Verified on a 7950X3D — finds the 96 MB / 32 MB split |
| **Game Integrity policy engine** | ✅ Proven to keep Battlefield 6 ambient-only |
| **Mutation Ledger + journal** | ✅ 500 chaos trials green |
| **Safety ladder + restore points** | ✅ Done |
| **Environment Hazard Scan** | ✅ `godmode scan` runs on real hardware |
| Emulator + Android catalogs | ✅ NES → PS4, Google Play Games |
| Ambient lever set (EcoQoS, services, IRQ) | 🔨 Next |
| Measurement harness (PresentMon) | 🔨 Next |
| Stutter Forensics | ⬜ Planned |
| Overlay | ⬜ Planned |
| Autotune | ⬜ Planned |
| Console shell / Playnite | ⬜ Planned |

Try it — nothing is modified:

```powershell
dotnet run --project src/GodMode.Cli -- topology
dotnet run --project src/GodMode.Cli -- scan
```

## Documentation

| | |
|---|---|
| [**CHARTER.md**](CHARTER.md) | What this project will never do. Binding. |
| [docs/superpowers/specs/](docs/superpowers/specs/) | The design spec — Ambient/Contact, Performance Domains |
| [docs/MASTER-PLAN.md](docs/MASTER-PLAN.md) | Full architecture, subsystems, risk register, roadmap |
| [docs/REFERENCE-MACHINE.md](docs/REFERENCE-MACHINE.md) | The box everything is validated on |

## Building

```powershell
dotnet build
dotnet test
```

Requires .NET 10 SDK. Integration tests require a VM — **they will never run on your host**,
by design.

## Contributing

This is the people's project. GPLv3, free forever, no paid tier.

Two rules: read [the Charter](CHARTER.md) first, and **bring evidence**. Community profiles
without measurement data attached are marked `unverified` and never applied by default.

### Why GPLv3 and not MIT

GPLv3 places no restriction on *you*. Use it, modify it, run it, share it, build a business
around it — all fine. The only thing it prevents is taking GodMode, closing the source, and
redistributing it that way.

For this project specifically that isn't ideology, it's a safety property. GodMode runs as
LocalSystem in the same neighbourhood as kernel anti-cheat. If someone ships a closed fork
that *does* inject, or *does* load a driver, and still calls it GodMode, the damage lands on
users and on this project's standing with anti-cheat vendors — and that is the one thing
that cannot be undone. Copyleft means every fork stays auditable.

The plugin API and profile schema are permissively licensed so the extension ecosystem
stays wide open.

---

<div align="center">
<sub>Windows is a fine operating system that happens to be a poor host for a game.<br/>
GodMode does not fix Windows. It just asks it, politely and reversibly, to step aside.</sub>
</div>
