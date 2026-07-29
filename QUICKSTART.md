# Getting started

**GamerGod changes nothing until you tell it to.** Everything below is safe to try right now.

---

## Install it

Download `GamerGod-Setup.exe` from [Releases](https://github.com/kosherplay-betatester/GamerGod-Microsoft-Sucks-Edition/releases)
and run it. That's the whole thing.

It will ask for administrator, because it registers a background service whose only job is to
put your machine back if something crashes. It tells you everything it's about to do before it
does any of it.

<details>
<summary>Prefer not to run an installer?</summary>

```powershell
git clone https://github.com/kosherplay-betatester/GamerGod-Microsoft-Sucks-Edition
cd GamerGod-Microsoft-Sucks-Edition
pwsh install\Build-Installer.ps1
pwsh install\Install-GamerGod.ps1
```

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The installed program doesn't —
it's self-contained.

</details>

---

## Two commands worth running first

Open a terminal — press `Win`, type `terminal`, press Enter.

### `gamergod scan`

Checks your PC for things quietly costing you frames, or stopping games from launching.

It takes about a second and **changes nothing**. On the machine this was built on it
immediately found a driver that would block Memory Integrity, and pointed out that Wi-Fi was
the only connection — which for competitive multiplayer matters more than anything software
can do.

You'll get findings ranked by how much they actually cost you, each with a plain explanation
and what to do about it. If your PC is in good shape, it says so and stops. It won't invent
problems to look busy.

### `gamergod topology`

Draws your CPU and shows which cores your games will get.

```
  AMD Ryzen 9 7950X3D 16-Core Processor
  16 cores / 32 threads  ·  asymmetric cache (X3D-class)

  ╭─ D0 ── PERFORMANCE ──────────────────────────── GAME ╮
  │  96 MB L3   8 cores / 16 threads   12 MB per core    │
  │  00 01 02 03 04 05 06 07 08 09 10 11 12 13 14 15     │
  │  ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██ ██     │
  ╰──────────────────────────────────────────────────────╯

  ╭─ D1 ── EFFICIENCY ────────────────────────── AMBIENT ╮
  │  32 MB L3   8 cores / 16 threads   4.0 MB per core   │
  │  16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 31     │
  │  ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒ ▒▒     │
  ╰──────────────────────────────────────────────────────╯
```

Amber is what your game gets. Grey is where everything else gets moved to.

Works on any CPU — AMD, Intel, ARM. If yours can't be split usefully, it says that plainly
instead of pretending.

---

## Common questions

**Will this get me banned?**

No. For any game with kernel anti-cheat — Battlefield 6, Valorant, Fortnite, Apex, Rainbow
Six, Call of Duty — GamerGod never touches the game. Not "we're careful about it": there is no
code path that can. It only moves *other* programs out of your game's way, which your game has
no way to see.

That's checked automatically on every build, and if an anti-cheat ever shows up that nobody
has catalogued, GamerGod assumes the strictest possible answer.

**Can it break my PC?**

Every change is written down before it's made, and undone when you're finished. If GamerGod
crashes, a background service undoes them. If that fails, **rebooting always works** — that's
guaranteed by design, not by GamerGod still running.

It will never suspend your fan or pump control software, so it can't cause an overheat. It
will never stop the services your controller, sound, or network need. Those lists can't be
overridden by any setting.

**Do I need to keep it open?**

No. Once the engine ships, it arms itself when a game starts and steps back out when you're
done. You open GamerGod to see what happened, not to make it happen.

**Does it phone home?**

Never. No analytics, no update pings, no install ID. Nothing about your PC leaves your PC.

**What does it cost?**

Nothing, forever. GPLv3, no paid tier, no bundled extras.

---

## Removing it

Add or Remove Programs, like any other app. Or:

```powershell
pwsh "C:\Program Files\GamerGod\install\Uninstall-GamerGod.ps1"
```

Your machine is restored **before** anything is deleted, and you get a receipt showing exactly
what was put back. Add `-KeepData` to keep your measurement history.

---

## If something goes wrong

**Reboot.** Every change GamerGod makes is undone at startup by design, and that doesn't depend
on GamerGod working.

Then [open an issue](https://github.com/kosherplay-betatester/GamerGod-Microsoft-Sucks-Edition/issues)
with the output of `gamergod scan`. It's read-only, so it's safe to share — though it does list
your hardware, so look it over first.

---

## Where it's up to

The engine works and is verified on real hardware. It doesn't optimise anything yet — right
now it's a very good diagnostic. Progress is in [the README](README.md), and what it will
never do is in [the Charter](CHARTER.md).
