# Product Direction

Decisions, not options. Written after building the classifier, the policy engine and the
ledger — which changed my mind about several things in the original plan.

---

## The uncomfortable truth about the category

Every tool in this space dies the same way. It ships a pile of toggles, wins a burst of
attention, and then nobody opens it again — because after the first session there is no
reason to. The tweaks are applied; the app is furniture.

GodMode has exactly one structural advantage over that pattern: **it produces new
information every time you play.** Not a setting. An answer. That is the product.

Everything below follows from taking that seriously.

---

## Decision 1 — The home screen is the stutter feed, not the core map

The core map is the best-looking thing we have and it is the wrong home screen. It shows
the same picture every day. Nobody reopens an app to see a static diagram of their own CPU.

The stutter feed changes every session, is specific to what the user just played, and
answers the one question every PC gamer has actually asked out loud: *"why did it do
that?"* It is the only screen with a reason to be looked at twice.

**Do:** stutter feed on launch, ranked by total frametime cost, since last session.
**Demote:** the core map to a panel inside it, and to the moment it matters — first run.

## Decision 2 — Zero configuration. One switch.

"Pick a profile" is a failure state. If GodMode needs the user to choose a profile before
it helps, it has offloaded its actual job onto them.

**The default path has one control.** Detect the machine, detect the title, apply ambient,
revert on exit. Profiles exist for people who go looking for them, and the autotuner
eventually removes the need even for those.

A corollary that matters: **the app should not need to be open.** Arm on game launch, act,
revert, and leave a receipt. The user opens GodMode to review what happened, not to make it
happen.

## Decision 3 — Ship the Proof Run in v1, not in milestone 4

Nobody trusts a performance tool, and they are right not to. The category earned that.

So the first thing a new user does is not "turn it on." It is:

> **Proof Run** — play for 90 seconds normally, then 90 seconds with GodMode. Here is your
> delta, on your hardware, with error bars. If it is zero, we will say so.

That flow is the entire brand argument in three minutes, it is the inverse of every snake
oil tool, and it costs nothing extra because the measurement harness is being built anyway.
Moving it earlier changes it from a feature into the onboarding.

**Consequence:** measurement moves ahead of the console shell in the roadmap. Shipping
optimisation without measurement would make us the thing we are criticising.

## Decision 4 — Optimise for the starved machine, not the reference machine

A 7950X3D with 64 GB gains something real but modest. A 16 GB laptop, a handheld, or a
six-core machine with a browser open gains *far* more, because it is genuinely contended.

We have been designing for the top of the market because that is the hardware in front of
us. That is backwards. **The person GodMode helps most cannot afford to fix their problem
with money.** That is also the larger audience by an order of magnitude, and the one for
whom "the people's project" actually means something.

**Do:** make the 8 GB / 6-core / handheld case a first-class test target. Weight the
default profile toward memory and contention relief, not cache partitioning — cache
partitioning only exists on hardware that is already fast.

## Decision 5 — Cut scope hard. Three things, done completely.

The current plan has autotune, stutter forensics, quick resume, emulators, Android, a
three-tier overlay, a console shell, a community repo and three UI surfaces. That is four
products. Shipping all of it badly is the most likely failure mode.

**v1 is exactly three things:**

1. **Ambient partitioning** — evict everything else off the game's cores, EcoQoS the rest.
   Works on every CPU, safe on every anti-cheat, and it is most of the win.
2. **Measurement** — PresentMon, A/B with confidence intervals, the Proof Run.
3. **Stutter attribution** — the retention feature and the thing nobody else has.

Everything else is v2 and it is fine. Emulators, Android, quick resume, autotune and the
console shell all become dramatically easier once those three are solid, because they are
all consumers of the same three subsystems.

## Decision 6 — Build the CLI, the service and the overlay. Not the desktop app.

The desktop GUI is the least-used surface in a tool that is supposed to run while you are
in a game. Users live in the overlay and in the frontend.

**Order:** service and CLI (headless, scriptable, testable) → overlay → Playnite extension
→ desktop UI last, as a shell around what already works. The design study we produced is a
specification for that final surface, not a reason to build it first.

## Decision 7 — Every session ends with a receipt

The journal already records every change and every revert. Rendering it costs nothing:

> **Session ended.** 11 services suspended and restarted. 63 processes evicted and
> released. 2 registry values changed and restored. Nothing left behind. One item needed a
> retry — Windows Search took 4 s to come back.

This is the cheapest trust-building feature available and it converts our safety
architecture from a claim into something the user watches work, every single time. It also
makes a genuine failure visible instead of silent.

## Decision 8 — Make contributing a single click

A community profile repo with a manual pull-request workflow will receive approximately
zero submissions. The measurement data is already structured and already on disk.

**Do:** a "Share this profile" button that opens a pre-filled pull request containing the
profile plus its evidence. Friction is the only thing standing between us and a dataset no
competitor can match — measured deltas across thousands of real machines is the moat, and
it is one that only an open project can build.

---

## What I would kill

| Thing | Why |
|---|---|
| **Quick Resume** | Gated to titles with no anti-cheat, high engineering cost, high memory risk, and it competes with the OS. Genuinely cool, genuinely not the point. v2 at the earliest. |
| **Tier 3 companion HUD** | A phone-as-second-screen HUD is a lovely demo that almost nobody will set up. Build tiers 1 and 2. |
| **Desktop config UI in v1** | See Decision 6. |
| **The reboot tier entirely** | HAGS and global timer resolution break the instant-toggle promise and are both sign-uncertain. If the harness later proves a win, add them then. Not before. |
| **Profile inheritance / cascade** | Elegant, unnecessary until there are enough profiles to inherit from. |

## What I would add that is not in the plan

| Thing | Why |
|---|---|
| **First-run hazard report** | Our scan found a display driver in an error state, a vulnerable kernel driver, and two coexisting hypervisors on the reference machine inside ten seconds. Telling someone their display driver is broken is worth more than 2% more FPS, and it costs us nothing. Lead with it. |
| **"Why is this greyed out?"** | Every disabled control explains itself in one sentence. *"Contact routing is unavailable because Battlefield 6 uses kernel anti-cheat."* Users forgive limits they understand. |
| **A visible do-nothing state** | When GodMode determines it cannot help — single-domain CPU, nothing running, already optimal — it must say so plainly rather than inventing work. A tool that admits it has nothing to offer today is the one people believe tomorrow. |
| **Uninstall that proves itself** | Run the full revert, then show the receipt, then remove. Leaving cleanly is part of the promise, and it is the last impression. |

---

## The one-sentence version

**Stop building a control panel and build an instrument:** it arms itself, tells you what
just cost you frames, proves its own effect on your hardware, and leaves a receipt when it
gets out of the way.
