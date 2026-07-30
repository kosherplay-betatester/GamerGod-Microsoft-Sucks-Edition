# CLAUDE.md — GamerGod

Read [`CHARTER.md`](CHARTER.md) before writing any code. It is binding and Articles I–VI
cannot be amended. The design spec is `docs/superpowers/specs/2026-07-29-gamergod-design.md`.

## What this project is

A reversible, measurement-driven scheduling and suppression layer for Windows 11 gaming.
It partitions the CPU so a game gets the best cores, quiets everything else, proves the
benefit with real frametime data, and restores the machine exactly on exit or crash.

## Invariants — never violate these

1. **Ambient vs Contact.** Every `IMutation` declares `MutationVisibility`. `Ambient`
   changes only affect *other* processes or global OS state and are invisible to games.
   `Contact` touches the game process. **A Contact mutation may never be applied to a
   title with kernel anti-cheat** — the policy engine has no bypass, and tests assert it.

2. **Journal before apply.** `CaptureAsync()` → write to journal → `FlushFileBuffers` →
   `ApplyAsync()`. Never the other way round. A mutation type that cannot describe its
   own inverse must not exist.

3. **Revert never throws for "already reverted."** `RevertAsync` is idempotent and may be
   called from a cold process after a crash or reboot. A failed revert logs and the chain
   continues — it never aborts the remaining reverts.

4. **Least privilege on handles.** `PROCESS_QUERY_LIMITED_INFORMATION` for reads,
   `PROCESS_SET_LIMITED_INFORMATION` for CPU Sets. `PROCESS_ALL_ACCESS` must never appear.

5. **Banned APIs.** `WriteProcessMemory`, `CreateRemoteThread`, cross-process
   `SetWindowsHookEx`, `bcdedit`, any kernel driver, any AppX removal, any `StartType`
   change. Enforced by analyzer; do not add suppressions.

6. **Never derive topology from constants.** No "CCD0 is the cache die." Derive from
   `GetLogicalProcessorInformationEx`. WMI is insufficient — it reports only aggregate L3.

7. **Never claim an unmeasured benefit** in UI text, docs, or comments.

## Architecture

```
GamerGod.Core          pure domain — mutations, ledger, policy, catalogue, search,
                       measurement. NO OS calls. Engine/ and Measurement/ are namespaces
                       inside this project, not separate projects.
GamerGod.Windows       the ONLY project that P/Invokes. NativeMethods.txt is the audit surface.
GamerGod.Ui            the WPF desktop app
GamerGod.Cli           gamergod.exe — and the privileged broker the app shells out to
```

**Not built, and this list must not imply otherwise:** `GamerGod.Service` (LocalSystem broker,
watchdog, boot recovery) and `GamerGod.Sentinel` (per-session agent: hotkeys, overlay). The
elevation broker today is the CLI, invoked with `runas`. An earlier version of this section
listed both as though they existed, and that error was copied into the README before anyone
checked the solution file.

**`Core` must not reference `GamerGod.Windows`.** An architecture test enforces
this. It is what makes the chaos tests possible — they run the whole ledger against a
`FakeOs` with no admin rights and no real machine state.

## Conventions

- **.NET 10**, C# latest, nullable enabled, `TreatWarningsAsErrors`.
- Records for domain types, `ImmutableArray<T>` for collections crossing a boundary.
- `ValueTask` on interface methods that are usually synchronous.
- Source-generated `System.Text.Json` contexts only — NativeAOT must work.
- xUnit + FluentAssertions. Property tests via FsCheck for the ledger.
- Tests that touch the real OS: `[Trait("Host", "VM")]`, excluded from the default run.

## Testing

```powershell
dotnet test                                  # safe: unit + chaos + architecture
dotnet test --filter "Host!=VM"              # explicit same
dotnet test --filter "Host=VM"               # VM ONLY — never on a dev box
```

**Never run VM-tagged tests on the host.** They stop services and kill `explorer.exe`.

## Reference machine

Ryzen 9 7950X3D (96 MB + 32 MB L3), RTX 5070 Ti + AMD iGPU, 64 GB DDR5-6000, Win 11 25H2
build 26200, Secure Boot + TPM + VBS on, Battlefield 6 installed for kernel-AC validation.
Full profile: `docs/REFERENCE-MACHINE.md`.

Topology detection must produce exactly two domains here: D0 = 96 MB / LP 0–15,
D1 = 32 MB / LP 16–31. If it doesn't, the detector is wrong — not the machine.
