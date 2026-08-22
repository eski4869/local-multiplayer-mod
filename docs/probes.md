# Probes

A probe answers **one question** about a mechanism that cannot be inspected from
outside the running game. They ship switched off, and are turned on from the
settings file when a symptom needs explaining.

## Why they stay in the build

The obvious alternative is to write a probe when a symptom appears and delete it
afterwards. That was tried, and it costs more than it looks:

- The symptoms that need a probe are the ones that are **hard to reproduce on
  demand**. By the time a probe has been written, compiled and deployed, the run
  that showed the fault is gone and the conditions may not come back.
- Deploying means closing the game, which throws away the state that produced
  the symptom.
- Each round of "add a probe, reproduce, read, refine" is a full cycle. Three of
  them is an evening.

Shipping them switched off costs one boolean read per sample and nothing else.

## Turning one on

`eski4869.LocalMultiplayerMod.Settings.xml`, next to the mod:

```xml
<Diagnostics>
  <ScreenTracking>false</ScreenTracking>
  <Teleport>false</Teleport>
  <UpsideDown>false</UpsideDown>
</Diagnostics>
```

No rebuild is needed — edit, restart the game, reproduce. Output goes to
`crashlog.log` in the Jump King folder.

**Reset that file before reproducing.** It is append-only and shared with every
other mod, so a clean file is the difference between reading a finding and
searching for it.

| Probe | Answers |
| --- | --- |
| `ScreenTracking` | Has a player's tracked screen drifted from the screen its position is really on? Collision only searches the tracked screen ±1, so a drift of two or more means no ground is found at all — which reads in play as falling through the world |
| `Teleport` | What did the screen teleport resolve, per player, at the moment it fired: camera screen, real screen, links found, and the move that resulted |
| `UpsideDown` | What did the upside-down gravity resync produce — distinguishes a broken fix from one that never installed |

## Writing one

Four rules, each paid for by getting it wrong.

**1. Deduplicate per player, not globally.**

Players are sampled in turn, so a single "same as last time" check alternates
between them and never matches. `UpsideDownProbe` had exactly that bug and wrote
every frame, growing `crashlog.log` past 800 MB in one session — enough to be a
performance problem on its own.

```csharp
private static readonly string[] Last = new string[5];   // indexed by player
if (line == Last[playerNumber]) return;
```

**2. Log on change, not on tick.** A probe that prints every frame is unreadable
and expensive. `ScreenTrackingProbe` prints when the drift changes, so a silent
log is itself the answer: no drift happened.

**3. Bound the output when the state can persist.** A player stuck out of bounds
satisfies the condition forever, and its position changes every frame, so
deduplication will not save you. `TeleportProbe` caps lines per crossing and
resets the budget when the condition clears.

**4. Record in the prefix when the original can throw.** A Harmony postfix does
not run on the exception path, and the case worth seeing is often exactly the
one that throws. The screen teleport throws when it finds no valid link — a
postfix-only probe would have left no trace of the interesting case.

## Reading the result

Two questions come up every time, and both are answerable before looking at
values:

- **Did the probe install at all?** A probe that never ran and a mechanism that
  never fired look identical in an empty log. Print a line at install time when
  the target cannot be found.
- **Is the code being measured the code you read?** Ask Harmony first:

  ```csharp
  Patches info = Harmony.GetPatchInfo(target);
  ```

  A transpiler means the decompiled source is not what runs. See
  [third-party compatibility](third-party-compatibility.md).

## Removing one

A probe whose question is permanently answered should go. One whose question can
come back — "is the tracked screen still correct?" — is worth keeping switched
off, because the cost is a boolean and the alternative is rebuilding it under
pressure.

The whole feature is one settings section and three files, so it comes out in
one piece when the mod is released.
