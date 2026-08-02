# The GamerGod Charter

This is a constitution, not a roadmap. Everything below is a permanent constraint on
what this project may become. A pull request that violates the Charter is rejected on
sight, regardless of how many frames it wins.

If you fork GamerGod, you inherit the Charter. That is the point of the GPL.

---

## I. We will never break your games

**This outranks performance.** A build that gains 20% and breaks one anti-cheat is a
failed build. GamerGod's effect on a running game must be *observationally equivalent to
a normal Windows install* — same loaded modules, same handles, same registry view, same
devices, same environment. Only the machine around it gets quieter.

This is enforced architecturally by the **Ambient / Contact** classification
(see `docs/superpowers/specs/`), not by good intentions:

- **Ambient** changes are invisible to the game by construction. They are always allowed.
- **Contact** changes touch the game process. They are opt-in, and **hard-blocked for any
  title protected by kernel anti-cheat.**

For Battlefield 6, Valorant, Fortnite, Apex, Rainbow Six, or anything else running a
kernel anti-cheat, GamerGod runs **100% Ambient. It never opens a handle to the game.**

## II. We will never ship a kernel driver

Not for core parking, not for MSR access, not for timer resolution, not for anything.

Kernel anti-cheats boot with Windows specifically to catch third-party kernel drivers,
and are documented to conflict with them. A GamerGod driver would be the single most
likely cause of the exact problem this project exists to prevent. It would also
permanently destroy our ability to say "GamerGod cannot destabilize your system."

Any feature that requires a driver is not a feature. It is out of scope forever.

## III. We will never inject into, hook, or modify a game

No `WriteProcessMemory`. No `CreateRemoteThread`. No DLL injection. No cross-process
`SetWindowsHookEx`. No IFEO or `Layers` registry entries for a protected game's
executable. No overlay that injects.

At the telemetry level these are indistinguishable from a cheat. We do not do them, and
we do not make exceptions for "just this one useful case."

## IV. We will never collect telemetry

No analytics. No phone-home. No crash reporting to a server. No update ping. No
unique install ID. No "anonymous usage statistics."

GamerGod makes no outbound network connections except ones you explicitly initiate
(checking for a release, downloading a community profile). Everything it learns about
your machine stays on your machine.

## V. We will never weaken your security for frames

We will not disable Windows Defender real-time protection. We will not disable
VBS, HVCI, Secure Boot, TPM, or Core Isolation. We will not disable CPU
vulnerability mitigations. We will not disable Windows Update permanently.

These "tweaks" are widely recommended and some of them do win frames. They also break
games — Battlefield 6's Javelin anti-cheat *requires* Secure Boot and TPM 2.0 and lists
HVCI and VBS capability as requirements. Disabling them does not slow that game down;
it makes it refuse to launch.

GamerGod does the opposite: it **detects** these being off and warns you which titles
will not start.

*(Per-folder Defender exclusions for game install directories are the one adjacent thing
we offer — opt-in, journaled, reversible, and documented with the tradeoff stated plainly.
That is a choice you make, not one we make for you.)*

## VI. Nothing we do survives a reboot unless you said so

Every change is captured, journaled to disk before it is applied, and reverted on exit,
on crash, on logoff, or at next boot. This is proven by a property test that applies
random change sets, kills the process at a random point, and asserts the machine returns
to its exact prior state.

We do not edit the boot configuration store. We do not change service start types. We do
not remove Windows components. We do not "debloat."

## VII. We will never claim a benefit we have not measured

Every toggle in GamerGod displays a real measured delta with a confidence interval, taken
on **your** hardware — or it displays the word `UNMEASURED` in grey.

A tweak whose confidence interval straddles zero is reported as "no measurable effect,"
even when the internet insists otherwise. Especially then.

Community profiles without measurement evidence attached are marked `unverified` and are
never applied by default.

## VIII. GamerGod is free, forever

GPLv3. No paid tier, no "pro" version, no feature gated behind a purchase, no sponsored
tweaks, no bundled software, no affiliate links.

Builds are reproducible so that anyone can verify the signed binary matches this source.
For software that runs as LocalSystem, that verification is not optional — it is the
only honest basis for trust.

## IX. We build the conductor, not every instrument

Where a good tool already exists, we integrate rather than reimplement: Playnite for the
frontend, Intel PresentMon for frametime capture, RTSS for frame limiting, HidHide for
controller management. We do not fork them, bundle them silently, or compete with them.

## X. The user is always one keypress from Windows

Four independent escape paths, always: a keyboard hotkey, a controller combo, a
service-side watchdog, and reboot. The fourth must work even if the first three are
broken, because it is guaranteed by Article VI rather than by any running code.

> **Built today: three of the four.** The switch in the app and `gamergod off` both work.
> Reboot works — the service restores the machine before anyone signs in. And the **crash
> watchdog is armed**: the background service checks every five seconds whether the program that
> owns a session is still running, and puts the machine back when it is not. Measured on the
> reference machine at 2.1 seconds from killing the owner to a fully restored desktop, with
> nothing else running and nobody typing a command.
>
> It needs no channel from the session. The journal has recorded who owns each session since
> ownership existed — durably, before the first change is applied — so the fact a pipe would have
> carried is already on disk, in a file only administrators can write. That matters beyond
> tidiness: a process being killed does not get to send a message, and this is the escape path
> for exactly that case.
>
> **Missing: the panic hotkey and the controller combo.** The only hotkey in the product toggles
> the frame readout.
>
> This note exists because the article was being read as a description of the build rather
> than a commitment about it, and a promise of four escape paths is worth less than an honest
> count of the ones that exist. The commitment stands. The count is three.

---

## Amendment

Articles I–VI may not be amended. They are the reason the project is trustworthy.

Articles VII–X may be amended by a pull request that explains what changed and why,
and that is open for public comment for no less than 30 days.
