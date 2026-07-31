# GamerGod Design Spec

**Date:** 2026-07-29
**Status:** Approved
**Supersedes:** the X3D-specific portions of `docs/MASTER-PLAN.md`

---

## 1. What GamerGod is

A measurement-driven scheduling and suppression layer for Windows 11 that gives a running
game the machine it was promised, and gives it back when you're done.

It is **not** a tweaker, a debloater, or a registry script. The difference is that every
change is journaled and reversible, every claimed benefit is measured on your own
hardware, and no change is ever observable from inside a game.

**Four pillars:**

| | |
|---|---|
| **PARTITION** | Give the game the best cores and evict everything else — universally, across AMD, Intel, and ARM |
| **PROVE** | Nothing claims a benefit it hasn't measured on *this* machine, with error bars |
| **PRESERVE** | Every change journaled before it's applied; four independent restore paths |
| **PROTECT** | Never break a game — architecturally guaranteed, not promised |

---

## 2. The Game Integrity Contract

**The foundational invariant.** From inside a game process, a machine running GamerGod
must be indistinguishable from a normal Windows install.

Formally: the game's loaded modules, handle table, registry view, device list,
environment block, filesystem view, and network stack are byte-identical to vanilla.
Only the *rest of the machine* is different — quieter.

### 2.1 Ambient vs Contact

Every mutation is classified by one question: **can the game detect it, even in principle?**

```csharp
public enum MutationVisibility
{
    /// Changes only what OTHER processes do, or global OS preferences.
    /// The game cannot observe this by any means available to it.
    /// Always permitted, for every title, with no exceptions.
    Ambient,

    /// Touches the game process, or state keyed to the game's identity.
    /// Opt-in per title. HARD-BLOCKED when kernel anti-cheat is present.
    Contact,
}
```

| | Ambient | Contact |
|---|---|---|
| Suspend / EcoQoS **other** processes | ✅ | |
| Confine **other** processes to another domain | ✅ | |
| Stop background services | ✅ | |
| Power scheme, interrupt steering, network QoS | ✅ | |
| Suppress scheduled tasks / Automatic Maintenance | ✅ | |
| Close the shell, launch the frontend | ✅ | |
| GPU driver settings (global or per-app-profile) | ✅ | |
| Defender exclusion for a game folder | ✅ *(opt-in)* | |
| CPU Sets on the game process | | ⚠️ |
| Affinity on the game process | | ⚠️ |
| `Layers` / IFEO registry keyed to game.exe | | ⚠️ |
| Suspending the game (Quick Resume) | | ⚠️ |

### 2.2 The enforcement rule

```
IF the title is protected by kernel anti-cheat
THEN the permitted mutation set is exactly { m : m.Visibility == Ambient }
```

This is enforced in the policy engine's type signature, not by convention. `GameIntegrityPolicy`
returns an `AllowedMutationSet` that **cannot contain** a Contact mutation for a protected
title; the engine has no code path that bypasses it, and this is asserted by tests over
every shipped profile × every anti-cheat tier.

For **Battlefield 6 (Javelin), Valorant (Vanguard), Fortnite/Apex (EAC), Rainbow Six
(BattlEye)** — GamerGod never opens a handle to the game.

### 2.3 Why this isn't a compromise

Most of the available win is Ambient. On the reference 7950X3D, evicting every other
process onto the 32 MB domain, EcoQoS-ing the rest of Windows, steering interrupts away
from the game's cores, and closing the shell delivers the bulk of the benefit — without
GamerGod ever touching the game.

Contact mutations add a increment on top, and are available for the large majority of
titles that have no kernel anti-cheat. The two goals were never actually in tension; they
only looked that way before the levers were classified.

### 2.4 Anti-cheat tiers

| Tier | Detection | Policy |
|---|---|---|
| **K** — kernel anti-cheat | `vgk.sys`, Javelin driver, `EasyAntiCheat.sys`, `BEDaisy.sys`; services `vgc`, `EasyAntiCheat`, `BEService` | **Ambient only.** No handle to the game, ever. |
| **U** — usermode anti-cheat | VAC, EOS SDK module, Denuvo | Ambient + CPU Sets on game (least-privilege handle only) |
| **N** — none | default | Ambient + all Contact, including Quick Resume |

Detection uses `EnumDeviceDrivers` and service enumeration — **it never requires a handle
to the game process**, so tier resolution itself is Ambient.

### 2.5 Proving the contract

Four mechanisms, all tested:

1. **Vanilla Diff.** For a title, capture loaded modules, handle count, its registry view,
   device list, and environment block with GamerGod ON vs OFF. Assert identical.
2. **Compatibility Regression Suite.** A corpus spanning every anti-cheat vendor, launched
   automatically, verified to reach gameplay. Results published in-repo.
3. **Auto-heal.** If a title that previously launched cleanly fails while GamerGod is
   active, GamerGod reverts, quarantines the profile, permanently marks the title
   Ambient-only, and writes a report.
4. **Type enforcement.** §2.2, plus a Roslyn analyzer that fails the build on
   `PROCESS_ALL_ACCESS`, `WriteProcessMemory`, `CreateRemoteThread`, cross-process
   `SetWindowsHookEx`, and any `bcdedit` invocation.

---

## 3. Performance Domains — universal hardware model

Replaces all CCD-specific logic. One abstraction, every CPU.

```csharp
public sealed record PerformanceDomain
{
    public required int Id { get; init; }
    public required ImmutableArray<int> LogicalProcessors { get; init; }
    public required long L3Bytes { get; init; }
    public required int MaxFrequencyMhz { get; init; }
    public required DomainClass Class { get; init; }
    /// CPPC / favoured-core ranking within this domain, best first.
    public required ImmutableArray<int> PreferredCoreOrder { get; init; }
    public required ImmutableArray<SmtPair> SmtPairs { get; init; }
}

public enum DomainClass { Performance, Efficiency, LowPowerEfficiency }
```

### 3.1 Detection

`GetLogicalProcessorInformationEx` is the source of truth. WMI is insufficient — on the
reference 7950X3D it reports a single aggregate `131072 KB` L3 and cannot see the
96 MB / 32 MB split at all.

```
1. RelationCache      → every L3 cache: (GroupMask, CacheSize).
                        Distinct GroupMasks ARE the domains.
2. RelationProcessorCore → SMT sibling pairs; EfficiencyClass byte
                        (non-zero ⇒ Intel hybrid / ARM big.LITTLE).
3. RelationProcessorDie / RelationNumaNode → cross-check domain boundaries.
4. CPPC preferred-core ranking → PreferredCoreOrder.
5. Classify:
     EfficiencyClass varies      ⇒ hybrid: high class = Performance, low = Efficiency
     L3 size varies              ⇒ asymmetric cache (X3D): larger = Performance
     both uniform, >1 domain     ⇒ symmetric multi-die: rank by PreferredCoreOrder
     single domain               ⇒ no partitioning; Ambient levers still apply
```

**Never hardcode "CCD0 is the cache die."** Derive it. A wrong assumption here is a silent
20% regression, and firmware revisions have moved the ordering.

### 3.2 Resulting policy per hardware class

| Hardware | Domains | Game → | Ambient → |
|---|---|---|---|
| 7950X3D / 9950X3D *(reference)* | D0 96 MB, D1 32 MB | D0 | D1 |
| 9800X3D, 7800X3D | 1 | — | — (EcoQoS still applies) |
| 7950X / 9950X non-X3D | 2 symmetric | best by CPPC | other |
| Intel 12th–15th gen | P, E, (LP-E) | P | E / LP-E |
| Snapdragon X | prime / perf / eff | prime+perf | eff |
| Handhelds | 1 (+ TDP hooks) | — | — |

Same code path in every row. This is what "supports all hardware" means architecturally.

---

## 4. The Ambient lever set

Every one of these is invisible to games and works on all hardware unless noted.

| Lever | Mechanism | Notes |
|---|---|---|
| **EcoQoS demotion** | `SetProcessInformation(ProcessPowerThrottling, PROCESS_POWER_THROTTLING_EXECUTION_SPEED)` | Microsoft's own API — identical to Task Manager "Efficiency mode". Drops base priority to Low + flags EcoQoS; scheduler moves the process to efficient cores and lower clocks. Universal, perfectly reversible, invisible to games. **The cleanest lever in the project.** |
| **Domain confinement** | Job object with `JOB_OBJECT_LIMIT_AFFINITY` = ambient domain mask | Reversal is free: close the handle. |
| **Service suppression** | SCM stop only — **never** change `StartType` | Three lists: DENY (hardware/AC/security), ALLOW, UNKNOWN(=leave alone) |
| **Scheduled task + maintenance suppression** | Run `rundll32 advapi32.dll,ProcessIdleTasks` *before* the session, then suppress during | Pre-run is zero-mutation |
| **Power scheme** | `powercfg /duplicatescheme` → activate ours → restore original | Never mutates the user's plan |
| **Interrupt / DPC steering** | Device `Interrupt Management\Affinity Policy` → `IrqPolicySpecifiedProcessors`, mask = ambient domain | Requires device restart (~1 s, not a reboot). **Mandatory auto-rollback if the device fails to re-enumerate.** Excludes virtual adapters. |
| **Network QoS** | `New-NetQosPolicy` DSCP-tag game traffic, deprioritise others | Real multiplayer win; fully reversible |
| **GPU vendor control** | NVAPI / AMD ADLX / Intel IGCL | Low-latency mode, shader cache size + location, power mode. Per-game, reverted on exit |
| **Frame cap at VRR sweet spot** | RTSS shared-memory API | Row 2 of the honest ledger — the largest latency win available |
| **Working-set trim** | `EmptyWorkingSet` on ambient processes | Trims to standby; no data loss |
| **Notification suppression** | Focus Assist / quiet hours | Stops alt-tab-stealing toasts |
| **Shell teardown** | `PostMessage(Shell_TrayWnd, 0x5B4)` graceful exit, fallback `AutoRestartShell=0` + taskkill | ~0% FPS — it's a UX feature, and the UI says so |
| **Defender folder exclusion** | `Add-MpPreference -ExclusionPath` | **Opt-in only.** Journaled, reversible, tradeoff stated plainly |

---

## 5. The three flagship features

### 5.1 Autotune — closed-loop self-optimisation

A Thompson-sampling bandit over the lever space, using PresentMon frametimes as the
reward signal, running during real play.

- **Arms:** profile variants (domain policy, EcoQoS aggressiveness, service set, frame cap).
- **Reward:** composite of 1% low, 0.1% low, and frametime variance — not average FPS.
- **Convergence:** each play session is trials; over ~a week it settles on the empirically
  best profile for *that game on that machine*.
- **Guardrails:** never explores Contact levers on Tier-K titles. Never explores on titles
  the user marks "competitive." Any arm that triggers auto-heal is permanently retired.
- **Transparency:** the UI always shows which arm is active and why.

### 5.2 Stutter Forensics — "why did my game hitch?"

Rolling 60-second ETW ring buffer. Any frame exceeding 2× the running median triggers an
autopsy over that window.

- **Providers:** `Kernel-Process`, `Kernel-Thread`, `Kernel-Disk`, `Kernel-Memory`,
  `Kernel-Power` (DPC/ISR), `DxgKrnl` (GPU preemption, mode-set), plus PresentMon's stream.
- **Attribution:** which process woke, DPC/ISR spike and from which driver, page-fault
  storm, disk burst, GPU preemption, shader compile, mode-set.
- **Output:** plain English. *"Your 340 ms hitch at 21:14 was Windows Search indexing your
  Steam library."* One click: suppress it next session.

    > **NOT BUILT, and nothing in the codebase can do this.** Naming a culprit process needs
    > ETW correlation of context switches and DPCs back to a process, which is not
    > implemented. What ships is *pipeline-stage* attribution from PresentMon columns
    > (`src/GamerGod.Core/Forensics/StallCause.cs`): it answers "the GPU was busy for 31 ms
    > while the CPU waited" and cannot answer "it was WSearch".
    >
    > This example survived here while the README separately claimed four projects that did
    > not exist and the protection list held a process name no machine runs. The same failure
    > three times: a document describing an intention, read later as a description of the
    > build. Kept as a goal, marked so it cannot be quoted as a capability.
- **Ring dump:** on a severe hitch or crash, write the `.etl` so power users can open it
  in WPA.

This is WPA-grade analysis productised. Studios do it internally; nobody ships it to players.

### 5.3 Quick Resume

Suspend an entire game to RAM, switch titles, return instantly — real console Quick
Resume on PC.

- **Hard-gated to Tier N** (no anti-cheat of any kind). Suspension of a game is a Contact
  mutation and is *never* permitted on Tier K or U.
- Working set stays resident; `EmptyWorkingSet` is explicitly **not** applied to a
  quick-resumed title.
- Bounded by available RAM with a safety margin; refuses rather than pages.

### 5.4 The Overlay — a HUD that never injects

RivaTuner and MangoHud both work by **injecting a DLL into the game process** and hooking
its present chain. Charter Article III forbids that, and it is the single most common
cause of anti-cheat false positives and overlay-related crashes in the wild.

GamerGod gets the same HUD without ever touching the game, because **measurement and
rendering are separated**:

- **Measurement never injects.** All frame data comes from PresentMon's ETW stream —
  present timings, GPU work, display latency — captured out-of-process. This works on
  *every* title including Tier-K anti-cheat games, in exclusive fullscreen, with zero
  contact.
- **Rendering has three tiers**, chosen automatically:

| Tier | Mechanism | Works on | Injects? |
|---|---|---|---|
| **1 — Native overlay** *(default)* | Layered, click-through, always-on-top window (`WS_EX_LAYERED \| WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`) composited by DWM | Borderless & windowed — the large majority of modern titles | **No.** Zero contact with the game. |
| **2 — RTSS bridge** | Publish GamerGod metrics into RivaTuner's OSD via its shared-memory API | Exclusive fullscreen too | RTSS injects — that is the user's own pre-existing, consented install, never our code |
| **3 — Companion HUD** | Local-only page on `127.0.0.1`, opened on a second monitor, tablet, or phone | Anything, incl. exclusive fullscreen | No. No network egress — loopback only |

Tier 1 is the default and is Charter-clean by construction. Tier 2 is offered only if RTSS
is already installed, and is clearly labelled as handing off to RTSS. Tier 3 is the
streamer / multi-monitor path.

**What it shows that RTSS and MangoHud cannot:**

| Panel | Why it's new |
|---|---|
| Frametime graph + rolling 1% / 0.1% low | Table stakes, but computed from PresentMon rather than a hooked swapchain |
| **Frametime consistency bar** | The metric that actually correlates with "feels smooth". Averages hide stutter; this doesn't |
| **Per-domain CPU utilisation** — game domain vs ambient domain, side by side | Live proof the partition is working. Nobody has this because nobody else models domains |
| **Which cores your game is actually running on** | Turns "I set an affinity, I think it worked" into an observable fact |
| **Live stutter attribution** — `⚠ hitch 340 ms — WSearch` appearing in real time *(NOT BUILT — needs ETW process correlation; shipped forensics attributes a pipeline stage, not a process)* | **The differentiator.** Every other overlay shows you a number went bad. GamerGod tells you *what did it*, while you're still in the match |
| GamerGod status line — active profile, `AMBIENT-ONLY`, detected AC tier | You always know exactly what is and isn't being done to your game |
| GPU util / VRAM / clocks / power / temps, CPU per-core, RAM | Standard, via vendor SDKs (NVAPI, ADLX, IGCL) |
| Present-to-display latency, Reflex stats where exposed | Software-measured input latency without LDAT hardware |

**Always optional, always instant.** The overlay is off by default and toggles in a single
frame — it is a separate always-on-top window whose visibility is one `ShowWindow` call, so
there is no re-hook, no restart, and no cost when hidden (the render loop stops; PresentMon
capture continues silently so nothing is lost while it's off).

| Control | Default |
|---|---|
| Global hotkey — cycle `off → minimal → full → graph` | `Ctrl+Shift+O` |
| Controller combo — same cycle | `Guide + Y` |
| UI switch in GamerGod and in the Playnite frontend | per-profile default |
| Per-panel toggles | `minimal` = one unobtrusive line |

Hiding the overlay never disables measurement, and disabling measurement never disables
GamerGod's optimisations. The three are independent.

### 5.5 Transparency Panel

Desktop-side companion to the overlay: everything competing with your frames, ranked by
measured cost, with attribution from Stutter Forensics. Turning the invisible visible is
the feature that makes people trust the rest.

---

## 6. Environment Hazard Scan

A read-only diagnostic that runs on first launch and on demand. It found five real issues
on the reference machine within seconds (see `docs/REFERENCE-MACHINE.md`).

Detects: display/audio/input drivers in an error state, virtual display adapters,
coexisting hypervisors (a documented kernel-AC conflict source), Wi-Fi-only networking for
multiplayer, hybrid-GPU misrouting, security features that will block specific titles from
launching, and known-conflicting software.

**It changes nothing.** It reports, ranked by expected frametime cost, with a plain
explanation of each. Several of its findings are worth more than every lever in §4 —
telling someone their display driver is in an error state, or that wired Ethernet beats
every software tweak for multiplayer, is more valuable than 2% more FPS.

---

## 7. Architecture

Unchanged from `docs/MASTER-PLAN.md` §3–§4 and §7, which remain authoritative for:

- Process model (Service / Sentinel split, named-pipe broker)
- The Mutation Ledger, write-ahead journal, tiered revert, five recovery triggers
- Enter / game-launch / exit-crash sequences
- Testing strategy, including the chaos property test

Two amendments:

1. `IMutation` gains `MutationVisibility Visibility { get; }` (§2.1), and the ledger
   refuses to apply a Contact mutation without an explicit `ContactGrant` token issued by
   `GameIntegrityPolicy`.
2. All CCD-specific types are replaced by `PerformanceDomain` (§3).

---

## 8. Non-goals

Permanently out of scope, per `CHARTER.md`: kernel drivers, injection or hooking, telemetry,
disabling VBS/HVCI/Secure Boot/TPM/Defender/mitigations, `bcdedit`, AppX removal,
`StartType` changes, paid tiers.

Deferred past v1: reboot-gated levers (HAGS, global timer resolution) — they break the
instant-toggle premise and are both sign-uncertain; ship them only if the harness proves
a win. Handheld TDP control — integrate with existing tools rather than reimplement.

---

## 9. Success criteria

1. On the reference 7950X3D, a measured 1%-low improvement with a confidence interval that
   excludes zero, in at least three of five test titles.
2. Battlefield 6 launches, passes its Javelin handshake, and plays identically with GamerGod
   active — verified by Vanilla Diff.
3. Chaos property test green at 500 iterations.
4. Hard-reset during an active session leaves the machine clean at next boot.
5. Every peripheral present at enter is present at exit, across 20 cycles.
6. A user can go from cold boot into a game and back out using only a controller.
