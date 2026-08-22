# Making someone else's mod work with two players

Gimmick mods are written for one player, and correctly so. When a second player
exists, the assumption surfaces as state that should belong to a player but does
not. This is how that gets fixed from the outside.

**Nothing here modifies another mod.** Every technique is a Harmony patch
installed by this mod at run time, redirecting a read while the method executes.
The other mod's assembly on disk is untouched, so a Workshop update cannot
conflict with it, and uninstalling this mod restores the original behaviour
exactly.

All of it lives in [`GimmickStateCompat.cs`](../GimmickStateCompat.cs), with the
per-player storage in [`ScopedFieldStore.cs`](../ScopedFieldStore.cs).

## The single guarantee everything rests on

`PlayerScope.Current` is the player currently being processed, and it is **null
in single player by construction** — `PlayerUpdateScopePatch` returns early when
multiplayer is off, so the scope is never entered.

Every redirect below begins the same way:

```csharp
PlayerContext context = PlayerScope.Current;
if (context == null)
{
    return true;   // run the original
}
```

Single player is therefore untouched because the code is never reached, not
because a flag happens to be false. Keep it that way: never gate a redirect on
`IsMultiplayerEnabled` or a player count instead.

## Choosing the technique

The shape of the third party's state decides the approach. Work down this list;
the first row that matches is the one to use.

| Their state | Technique | Worked example |
| --- | --- | --- |
| Per-player **value** in a property | Patch the getter, keep a copy per player | `SwitchBlocks.Data.DataSand.HasEntered` |
| **The player itself** in a property | Patch the getter, return the scoped player | `JumpKing_Expansion_Blocks.ModEntry.Player` |
| Plain **field** (no accessor) | Patch a *method that reads it* | `TeleportBugFix.GetIndex0` → `GameLoop.m_player` |
| Plain field, refreshed by their own copy step | Call that copy step again inside each scope | `UpsideDownCore.Models.Manager.Update` |
| Their logic reads the local pad directly | Drive their entry point once per player | `SwitchBlocks` Jump switch |
| Genuinely shared world state | **Do not redirect it** | `DataSand.State` |

### 1. Per-player value in a property

The common case. Their singleton mixes two kinds of state: some describes the
world, some describes a player. Only the second kind is redirected, and each
player needs its own copy of it, so the values are held in `ScopedFieldStore`
keyed by owner identity.

Declared as a table entry:

```csharp
new ScopedType
{
    TypeName = "SwitchBlocks.Data.DataSand",
    PlayerOwned = new[] { "HasSwitched", "HasEntered" },
    PlayerOwnedCombined = new string[0]
}
```

`PlayerOwnedCombined` is for a property that is per-player to write but must read
as "true for any player" — a safety check that has to hold for everyone.

Key it on **owner identity, not type name**. `DataSand.Reset` drops the
singleton rather than clearing it, so a name-keyed store lets the next run
inherit the previous one's values.

### 2. The player itself in a property

When a mod caches `EntityManager.Find<PlayerEntity>()` once and its patches
consult that reference, the correct answer is always "the player being
processed". Nothing needs storing:

```csharp
private static bool ScopedPlayerGetterPrefix(ref PlayerEntity __result)
{
    PlayerContext context = PlayerScope.Current;
    if (context == null || context.Player == null) return true;
    __result = context.Player;
    return false;
}
```

One getter fixes every read site at once — twelve of them, across nine files, in
the case that prompted it.

### 3. Plain field

**A field cannot be patched.** There is no method to intercept. Go up one level
and patch whatever *reads* it:

```csharp
// TeleportBugFix.GetIndex0() reads GameLoop.m_player - always player 1
__result = (int)((0f - (context.Body.Position.Y - 360f)) / 360f);
```

Reuse their exact expression rather than an equivalent one. The point is to
change *which player* is being asked about, not what the answer means; a
different-but-equivalent formula can disagree on a boundary and turn a
redirection into a behaviour change.

### 4. Plain field refreshed by their own copy step

When a mod snapshots state into fields once per tick, the snapshot is taken
before any player's scope exists. Re-run their own copy method after installing
each player's scope, so it refreshes from that player's values:

```csharp
GimmickStateCompat.ResyncUpsideDown();   // calls UpsideDownCore's Manager.Update
```

### 5. Their logic reads the local pad

Additional players have no physical controller. Where a mod polls input itself,
enter each player's scope and call their entry point with that player's
`InputComponent`. See `JumpUpdatePrefix`.

### 6. Genuinely shared state

Some state is *correctly* global — which way a switch is currently facing, for
example. Splitting it changes what the map means. This is a design question, not
a defect, and the honest answer is to leave it alone.

## Recognising the defect

Three mods have now shown the same shape: **one static standing for "the
player", consulted by patches on base-game methods that run once per player per
frame.** The effect is decided from one body and applied to whichever frame is
running.

Search for it directly, including the base game's own static:

```bash
ilspycmd -o <outdir> -p SomeMod.dll
grep -rn "static PlayerEntity" <outdir>
grep -rn "GameLoop.m_player" <outdir>
```

The symptom is order-dependent: whichever player moves first decides for
everyone, so reversing who acts first often makes it work. That asymmetry is the
signature.

## Before trusting any decompilation

Ask Harmony what else is attached to the method:

```csharp
Patches info = Harmony.GetPatchInfo(target);
```

**A transpiler means the decompiled source is not what runs.** Analysis of the
teleport predicted one result while measurement gave another, with every visible
input matching, and none of it was resolvable until the patch list showed the
method body had been rewritten by another mod.

Ask at the first call rather than at install — mods patch during their own
level-load hook, and the order between mods is explicitly not a contract.

## Rules for adding a target

**Verify the name against the assembly.** Every target is a string; a typo
cannot be caught by the compiler.

```bash
ilspycmd -l c SomeMod.dll | grep -i "TheType"
```

**Report what was missed.** A type that is absent because the map does not use
that mod, and a type that is absent because the name is wrong, look identical
from inside. Startup always states how many targets were scoped and names the
rest:

```
Local Multiplayer gimmick compatibility: scoped 30 targets; unavailable targets: DataAuto.HasSwitched
```

A silently skipped target once cost two rounds of testing measuring a layer that
had installed nothing. The count moving is also the quickest confirmation that a
new target took.

**Leave their deliberate behaviour alone.** `TeleportBugFix` has a second path,
selected by a map tag, that resolves through `Camera.CurrentScreen` — which
`PlayerScope` already makes per-player. That path is their fix for maps that
asked for it, and it is skipped rather than overridden.

## What this does not scale to

The tables are hand-maintained, per mod, per member. This covers the mods that
have been diagnosed, not the Workshop. Adding one is deliberate work that starts
from a symptom and a measurement — the procedure is in the workspace repository
under `docs/gimmick-diagnosis.md`.
