# Overlay UX and copy

**Date:** 2026-07-30
**Status:** Approved, unimplemented
**Supersedes:** the UX and copy portions of `2026-07-29-gamergod-design.md` §5.4. The tier
table in that section stands; the panel list and the `⚠ hitch 340 ms — WSearch` example
in it do not — see §9.

This is written before the code exists so that the code cannot quietly decide these
questions by accident.

---

## 1. The two limitations, stated first

Both fall straight out of Charter Article III, and neither is going to be engineered away.

**It cannot draw over a game running in Fullscreen.** Every mainstream overlay — RTSS,
Steam, Discord, Afterburner — draws by loading its own code into the game and taking over
the game's presentation. Article III forbids that absolutely. What is left is a window of
our own, always on top and transparent to clicks, and Windows will not put any window of
ours above a game that has taken the whole screen. Most modern titles default to
Borderless, where this works. Some do not, and some players deliberately choose Fullscreen.

**It can cost the frames it is measuring.** On Windows 11 a top-most transparent window
can stop the game handing its frames straight to the display and route them through the
desktop compositor instead, which costs roughly a frame of delay. So the honest overlay
degrades the thing it exists to observe.

The second one is not a footnote to be buried. GamerGod already parses PresentMon's
`PresentMode` column, and `StallCause.LeftIndependentFlip` in
`src/GamerGod.Core/Forensics/StallCause.cs` detects exactly this transition. **We are the
only overlay that can measure its own cost and say so.** Every design decision below is
downstream of taking that seriously.

---

## 2. What it shows by default

**The organising rule: the live readout carries only what changes.**

Everything constant during a session — the anti-cheat tier, the active profile, which
cores the game got, the core map — belongs to the launch receipt and the session report,
not to a line of text a player is trying to read while being shot at. That rule alone
removes about two thirds of the panel list in the old §5.4, and it removes it for a reason
that will still be true in a year.

Default (`OverlayDetail.Minimal`, and it is the default because it is the cheapest thing
that answers a question):

```
GG    141 FPS    1%  96    HITCH  7
```

| Field | Why it survived the cut |
|---|---|
| `GG` | Whether GamerGod is armed. One bit, and the only bit the player cannot learn without leaving the game. Amber when armed, grey when not. |
| `141 FPS` | The number every player already has a feel for. |
| `1%  96` | The 1% low over the last 60 seconds — `FrameSeries.OnePercentLowFps`, the same statistic reviewers quote, so it is comparable with numbers from anywhere else. This is the number that says whether it is *smooth*, which is the actual question. |
| `HITCH  7` | Hitches this session, by the existing definition: slower than twice the median. It is the only field that moves when something bad happens, and it is the hook into the report afterwards. |

Everything else is opt-in and lives behind the same hotkey cycle already specified —
`off → Minimal → Full → Graph`:

- **Full** adds median frametime in ms, per-domain CPU load (game domain vs ambient
  domain), which cores the game is actually on, and the anti-cheat tier.
- **Graph** adds a five-second frametime trace.

Both are larger windows, and window size is compositor work, which is cost. So the cost
check in §4 (iii) re-runs on every level change, and a player who moves to Graph after
being warned is making that trade with the number in front of them.

**Rules the readout obeys:**

- `font-variant-numeric: tabular-nums`, Cascadia Mono, every digit. A readout that
  reshuffles its own columns at 140 Hz reads as a toy.
- **Updates twice a second, not every frame.** A number that changes 140 times a second is
  not readable, and each change is a repaint the compositor has to carry.
- **No animation. Ever.** No fade in, no slide, no pulse. The only thing that changes is
  digits.
- Backing plate `#0D1017` at 60% with a one-pixel `#262D3B` border. No drop shadow —
  a shadow over arbitrary game art looks like a smudge, and it is more to composite.
- Default position top-left, movable to any corner, remembered per game. Top-left because
  that is where RTSS and Afterburner put it and therefore where the player's eye already
  goes; per-game memory because no corner is free in every game.
- **Never a zero standing in for an unknown.** See the loading state, §4 (v).

---

## 3. Live stutter attribution: argued both ways, then decided

**For putting it in the live readout.** It is the differentiator. Nobody else can tell you
*why* a frame was late, the answer is most vivid while the moment is fresh, and it is the
one thing that makes this worth having over RTSS.

**Against.** Four things, and they are not close:

1. **The player cannot act on it.** Mid-match, the set of available actions is empty.
   "The GPU was busy for 36 ms of a 40 ms frame" changes nothing you can do in the next
   ten seconds.
2. **It costs most exactly when it can least afford to.** Drawing a new line of text is a
   relayout and a repaint. A live cause appears at the instant of a hitch — so the overlay
   does its most expensive work on the frame that was already late, and on the next few.
3. **The honest answer is often no answer.** `StutterAttributor` is deliberately
   conservative and returns `Unattributable` whenever a column was `NA`. "Not attributable
   from the columns captured", flashing over a firefight, is worse than silence.
4. **Live classification is weaker classification.** A hitch is defined against the
   capture's median. Live, that median is a rolling estimate, so the same frame can be
   classified two ways depending on when you asked.

**Decision: the cause goes in the post-session report. The live readout gets the count
only.** In Graph mode the spike is drawn, because the trace is drawing anyway.

**With exactly one exception, and it is the whole point of this document: the one cause
the live readout does announce is the one GamerGod caused itself.**
`StallCause.LeftIndependentFlip`, while our own readout is on screen, is the single
attribution that fails every objection above — the player *can* act on it, the action is
one keypress, and the cost of saying it is trivially smaller than the cost of not saying
it. The rule reads well because it is true: *the only stutter we interrupt you about is
the one we are causing.*

---

## 4. States and copy

Exact strings. Sentence case, British spelling, no apology, no exclamation marks — the
voice already in `MainWindow.xaml`.

### (i) Before enabling — the Settings toggle

Lives in a new `OVERLAY` card on the Settings page, above `BEHAVIOUR`.

> **Show frame rate over your game**
>
> A small readout in the corner: frames per second, your 1% low, and how many hitches this
> session.
>
> It appears in games set to Borderless or Windowed, and not in games set to Fullscreen,
> because GamerGod draws it in a window of its own instead of drawing inside your game —
> and Windows will not put any window above a Fullscreen game.
>
> On some games the readout costs about a frame of delay by itself. GamerGod measures that
> on your machine and tells you when it happens.

Article VII line beneath it, in `Mono`, `InkFaint` — this is the only toggle in GamerGod
whose measured delta is a cost, and it earns the badge like everything else:

> `UNMEASURED · nothing has measured what this costs on your machine`

After a session where nothing changed:

> `MEASURED · Battlefield 6, 41 min · no change to how frames reached the screen`

After a session where it did (this line in `Signal` amber):

> `MEASURED · Battlefield 6, 41 min · +8.2 ms before frames reached the screen`

Disabled variant, one sentence, per the standing rule that a greyed control explains
itself:

> `The readout is unavailable because Intel PresentMon is not installed — it is what measures the frames.`

### (ii) The game is in Fullscreen and the readout cannot be seen

Never a toast during play: a notification is itself a compositor event and can knock the
game out of independent flip, which would mean warning the player about a cost by
inflicting it. This lands in three places that cannot interrupt — the Dashboard when they
come back, the session receipt, and the Settings status line.

> **The readout is running, and Battlefield 6 is covering it.**
>
> Battlefield 6 is set to Fullscreen. Windows gives a Fullscreen game the whole screen and
> puts nothing on top of it, GamerGod included, so the readout is behind the game where
> you cannot see it.
>
> Switching the game to Borderless brings it back. In most games that setting is under
> Video or Display, near the top, called Display Mode or Window Mode.
>
> Nothing is lost either way: frames are still being measured, and your session report will
> be complete whether or not you could see the numbers while you played.

Actions: `SWITCH TO BORDERLESS AND I'LL LOOK AGAIN` is not offered — GamerGod does not
change a game's settings. The buttons are `SEE THE SESSION REPORT` and
`STOP TELLING ME THIS FOR BATTLEFIELD 6`.

An honest paragraph appended only when the comparison is likely to be on the player's
mind, because they will otherwise assume this is a bug we could fix:

> Steam's overlay and RivaTuner can draw over a Fullscreen game because they run their own
> code inside it. GamerGod will not do that to a game — it is the same rule that keeps it
> safe with anti-cheat, and this is what it costs.

Appended only when RTSS is already installed (Article IX, and the honesty about whose code
is doing what is not optional):

> RivaTuner Statistics Server is already on this machine. GamerGod can send these numbers
> to RivaTuner's own on-screen display, which does draw over Fullscreen games. RivaTuner
> does that by running inside your game — that is its code and your install, not ours.
> `SEND THE NUMBERS TO RIVATUNER`

### (iii) The readout is measurably costing frames

The one that matters. Two surfaces, because a click-through window cannot receive a click
(see §6) — the in-game notice cannot have a button, and pretending otherwise is how this
gets designed wrong.

**In the readout, replacing the numbers for ten seconds, once per session:**

```
This readout is adding 8.2 ms before your frames reach the screen.
Ctrl+Shift+O hides it.  Xbox button + Y on a controller.
```

**In the GamerGod window, on the Dashboard, and at session end:**

> **This readout is costing you frames.**
>
> When it is on screen, Windows stops handing your game's frames straight to the display
> and routes them through the desktop instead. GamerGod measured the difference here, in
> this session:
>
> ```
>                        frames/s   frame to screen   sampled
> without the readout         141            6.1 ms    4m 12s
> with the readout            139           14.3 ms    3m 40s
> ```
>
> The frame rate barely moved. The delay did: 8.2 ms more between a frame being finished
> and reaching your screen. That is felt as input lag, not as stutter.
>
> Other things cause this too — a notification, another overlay, alt-tabbing. GamerGod is
> telling you about this one because it is the one it can switch off.

Actions:

- `TURN THE READOUT OFF` — primary
- `KEEP IT ON` — ghost
- `SEE THE FRAMES` — ghost; opens the report filtered to the `LeftIndependentFlip` frames,
  because a claim like this should come with the rows behind it
- `Stop telling me this for Battlefield 6` — a checkbox, listed and reversible in Settings

The last paragraph is doing specific work. We detected a transition, not a culprit. Saying
"we are costing you frames" flatly would be the same unmeasured confidence the rest of the
product refuses, so the copy says what was measured and then says why we are the one
raising our hand.

### (iii-b) Invisible *and* costly — the worst case, and the only place GamerGod acts alone

These co-occur more often than they sound: a top-most transparent window can be the very
reason a game loses independent flip, whether or not you can see it. So the player can be
paying for a readout that is behind the game.

A readout nobody can see has no benefit to weigh against a measured cost. That is the one
condition under which GamerGod stops drawing without asking — it is not overriding a
choice, it is removing something with a measured cost and provably zero benefit. It says
so afterwards, in the receipt:

> **Stopped drawing the readout.**
>
> It was behind Battlefield 6 where you could not see it, and it was adding 8.2 ms before
> your frames reached the screen. Frames kept being measured and this report is complete.
>
> Switch Battlefield 6 to Borderless and turn the readout back on whenever you want it.

Settings status line while this is in effect:

> `PAUSED · not drawn in Battlefield 6 — it was invisible and costing 8.2 ms`

### (iv) PresentMon is not available

Without PresentMon there are no frame times, so there is no readout at all — and, more
awkwardly, no way to know why. Every sentence about the cause must read as a guess.

> **Frame rate cannot be measured on this machine.**
>
> GamerGod reads frame times from Intel PresentMon, which is not installed here. Nothing
> is injected into your game either way — PresentMon reads events Windows already
> publishes.
>
> `winget install --id Intel.PresentMon.Console`
>
> Until it is installed, GamerGod cannot tell whether a game is running in Fullscreen or
> Borderless. If the readout does not appear, Fullscreen is the likely reason — that is a
> guess from what usually causes it, not something GamerGod has checked.
>
> It also means GamerGod cannot notice if the readout is costing you frames. That check
> reads the same data.

Actions: `INSTALL PRESENTMON` (through the Windows Package Manager, the same path as
Get more) and `COPY THE COMMAND`.

The last paragraph is the uncomfortable one and it stays. A player who installs nothing
should know that the safety net described in (iii) is not running.

### (v) Loading — the first two seconds

`PresentMonCapture` discards the first two seconds of every capture, so for two seconds
there is genuinely nothing to say:

```
GG    SAMPLING
```

Not `0 FPS`. A zero for two seconds is a false statement, and it is the exact class of
small lie this product is built to avoid.

### (vi) Empty — no game running

The readout is not drawn at all. No zeros, no empty frame, no ghost window. Settings shows:

> `IDLE · nothing is being measured — the readout appears when a game starts`

### (vii) Nothing useful to offer

The most neglected state and the most revealing. A clean session, at session end:

> **Nothing hitched.**
>
> 41 minutes of Battlefield 6, 147,300 frames, and not one took more than twice as long as
> the rest. There is nothing to explain.
>
> `median 7.1 ms · 1% low 96 fps · steadiest 0.4% of sessions on this machine`

And the harder version, when the measurement itself came up short — which happens per
title and per driver, because PresentMon writes `NA` for columns it could not fill:

> **7 hitches, and GamerGod cannot say what caused them.**
>
> The columns that would answer it were not filled in for this game. GamerGod would rather
> tell you that than pick the most likely-looking answer — a confident wrong cause sends
> you off fixing a problem you do not have.

---

## 5. The thing that should not be built

**Do not automatically hide a readout the player can see.**

Everyone reads §4 (iii) and asks the same question: if you already know it is costing
frames, why not just turn it off? Five reasons, and they compound:

1. **It destroys the evidence at the moment it becomes interesting.** The A/B in the
   dialog exists because both arms were sampled. Auto-hiding deletes the "with" arm and
   leaves a claim with nothing behind it.
2. **It oscillates.** Coming back out of composition costs a frame too. A threshold rule
   would hide, regain independent flip, lose its own signal, and have to guess whether to
   re-show. Every hysteresis constant in that loop is a number nobody measured.
3. **An overlay that vanishes on its own reads as a crash.** It gets filed as a bug, and
   the player works around the tool rather than trusting it.
4. **It converts a measurement into a policy.** The entire product argument is that we
   measure and you decide. The first time we quietly decide, the argument is retired.
5. **It is the smallest possible version of the thing every tool in this category does** —
   act on your machine for reasons it does not show you.

The narrow exception in §4 (iii-b) is not a softening of this. There, the benefit is
provably zero because the readout is behind the game, so there is no trade to make and
nothing for the player to weigh. Here there is, and it is theirs.

*Runner-up, for the record:* the GPU temperature / clock / VRAM / power panel from the old
§5.4. Three vendor SDKs (NVAPI, ADLX, IGCL), three release cadences, it makes the window
wider, width is cost, and HWiNFO and the vendor tools already do it better — Article IX
says integrate rather than reimplement. Deferred, on its own merits, not smuggled in as
part of this.

---

## 6. Interaction, and one consequence that changes the design

The readout is `WS_EX_TRANSPARENT`. **It therefore cannot receive a click.** That is not a
detail to discover during implementation:

- No in-game notice may have a button. The in-game affordance is a keypress and a
  controller combo, always named in the notice itself.
- Every button in §4 lives in the GamerGod window, on the Dashboard, or in the session-end
  card.
- Making the notice temporarily hit-testable is not an option: it would steal input from
  the game, which is worse than every problem it solves.

| Control | Binding | Notes |
|---|---|---|
| Cycle `off → Minimal → Full → Graph` | `Ctrl+Shift+O` | As already specified in §5.4 |
| Same cycle, on a controller | Xbox button + Y | `Guide + Y` internally; the copy says "Xbox button" because that is what is printed on it |
| Move the readout between corners | `Ctrl+Shift+Arrow` | Remembered per game |

**The escape is always visible.** For six seconds after the readout appears — and after any
level change — it carries its own dismissal:

```
GG    141 FPS    1%  96    HITCH  7    Ctrl+Shift+O hides this
```

Then the hint drops and the readout goes quiet.

Hiding the readout never stops measurement, and stopping measurement never stops
GamerGod's optimisations. Three independent things, and the copy never implies otherwise.

---

## 7. Copy rules for whoever implements this

- **The noun is "the readout".** "Overlay" is used only in the section header and where a
  comparison to Steam or RivaTuner is being drawn, because those overlays work by a method
  ours is forbidden to use and the word blurs that.
- **No cause ever names a process.** `StutterAttributor` names a stage of the present
  pipeline, deliberately. The `⚠ hitch 340 ms — WSearch` example in the old §5.4 claims an
  attribution nothing in this codebase measures, and it must not ship in that form.
- **No zero standing in for an unknown**, anywhere, ever. `—` or the word.
- **"Hitch"** is the word, everywhere. Not stutter, not spike, not frame drop.
- **Delay is stated in milliseconds, frame rate in whole frames per second.** No "about a
  frame", no percentages of a percentage.
- **Never a projected figure**, including for the overlay's own cost. Before a session has
  measured it, the answer is `UNMEASURED`, even though we are fairly sure what it will say.

---

## 8. What implementation has to verify before any of this is true

Copy that asserts a measurement is a lie until the measurement exists. In build order:

1. `LeftIndependentFlip` fires on the reference machine when the readout is shown over a
   Borderless game, and does not fire when it is hidden. If it does not separate cleanly,
   §4 (iii) has no basis and the dialog does not ship.
2. The before/after latency figures come from `MsRenderPresentLatency` sampled on our own
   timeline — we know exactly when the window appeared — with both sample durations shown.
   If either arm is under 60 seconds, the dialog says so instead of quoting a delta.
3. Fullscreen detection is from `PresentMode`, never from window rectangles. Without
   PresentMon it is not detected at all, and §4 (iv) is the copy.
4. The auto-stop in §4 (iii-b) requires *both* conditions measured — invisible *and*
   costly. One without the other does nothing.

---

## 9. Changes to the previous spec

| §5.4 as written | Status |
|---|---|
| Three rendering tiers | Stands. Tier 3 remains cut per `PRODUCT-DIRECTION.md`. |
| "Live stutter attribution — the differentiator" in the overlay | **Overturned.** Report only, with the one exception in §3. |
| `⚠ hitch 340 ms — WSearch` | **Withdrawn.** No process attribution is measured. |
| GPU/VRAM/clock/power/temps panel | **Deferred**, §5. |
| Present-to-display latency, Reflex stats | Deferred with it, same reason. |
| "no cost when hidden" | **Needs qualifying.** True for the render loop. Not established for the window's existence — §8.1 is what settles it. |
| Off by default, instant either way | Stands, and `UiSettings.OverlayEnabled` already reflects it. |
