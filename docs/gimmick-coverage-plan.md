# Gimmick coverage: what to add next, and what deliberately needs nothing

A worked-out plan for extending `GimmickStateCompat` to the gimmick mods that
popular maps actually require. Every mod below was inspected by loading its
DLL through reflection (with `JumpKing.exe` preloaded so its types resolve)
and enumerating **mutable static fields** — the shape that breaks with two
players, because one box cannot hold two players' values at once.

Read `third-party-compatibility.md` first for how scoping works. This file is
the decision, not the mechanism.

## Why these mods and not others

Sampled from the Level category by popularity, not the Mod category. What
matters is what maps *require*, since an installed-but-unused mod changes
nothing. See `docs/workshop-ecosystem-index.md` in the workspace repo for the
crawl and its caveats.

| Mod | Required by (of 20 sampled maps) | Status |
| --- | --- | --- |
| JumpKingPlus blocks | 15 | **Needs nothing** — see below |
| Forced Slope Blocks | 6 | **Needs nothing** — no mutable static state at all |
| Movement Control Blocks | 4 | **Needs work** — three static holders |
| Trap Sand Blocks | 3 | **Probably needs nothing** — verify first |
| Switch Blocks | 2 | Already covered (8 types) |
| Expansion Blocks | 7 | Already covered |

## The finding that matters: most of these need nothing

Being widely required is a reason to look, not a reason to act. Four of the
five most-required mods hold no per-player state worth scoping:

**Forced Slope Blocks** — one type, zero mutable statics. A slope is a
collision shape, not a state machine. Nothing to do, and nothing will change
that short of the author adding state.

**JumpKingPlus blocks** — two mutable statics, neither per-player:
- `JumpKingPlusBlockFactory.Flags` (`String[]`) — level-parse configuration,
  written once at load, read the same by both players.
- `JumpKingPlusEntry.WarpTransition` (`JKSound`) — a sound asset handle.

Its blocks (`LowGravityBlock`, `OneWayBlock`, `ThinSnowBlock`, `WarpBlock`)
are behaviours that act on the body passed to them, holding no per-player
field of their own. **The most-required gimmick mod in the ecosystem needs no
scoping**, which is worth stating plainly because the opposite was assumed.

**Trap Sand Blocks** — three mutable statics on `FactoryTrap`:
`LastUsedMapId`, `LastUsedMapIdUp`, `LastUsedMapIdDown` (all `UInt64`).
These are level-load bookkeeping ("which map did I last build blocks for"),
not gameplay state — they change when a level loads, not when a player moves.
`PatchBodyComp.KnockedRef` is `readonly`, so it is a reference installed once
rather than state that varies.

> **Verify before acting on Trap Sand.** The reasoning above is from field
> names and types, not from reading what writes them. If a two-player session
> on a trap-sand map shows one player's trap state affecting the other,
> `LastUsedMapId*` is where to look first — but do not add scoping for it
> speculatively, because scoping level-load bookkeeping per player would
> break level loading rather than fix anything.

## The one that needs work: Movement Control Blocks

Three mutable statics, each a single instance standing in for "the player":

| Type | Static field | Type |
| --- | --- | --- |
| `MovementControl.ModEntry` | `Data` | `DataMomentumStop` |
| `MovementControl.Patches.PatchJumpState` | `BehaviourForcedNeutral` | `BehaviourForcedNeutral` |
| `MovementControl.Patches.PatchPadInstance` | `BehaviourInvertInput` | `BehaviourInvertInput` |

`DataMomentumStop` holds exactly one writable property: `Screen` (`Int32`).

**This is the classic shape.** `Screen` means "the screen the momentum-stop
rule applies on", and with two players on different screens, whoever writes it
last decides for both — the order-dependence signature described in
`docs/gimmick-diagnosis.md` (workspace repo).

### What to implement

Add to `GimmickStateCompat.Targets`, following the existing entries exactly:

```csharp
new ScopedType
{
    TypeName = "MovementControl.Data.DataMomentumStop",
    PlayerOwned = new[] { "Screen" },
    PlayerOwnedCombined = new string[0]
},
```

`PlayerOwned`, not `PlayerOwnedCombined`, because `Screen` is a value each
player has independently — there is no sensible way to fold two players' screen
numbers into one answer. `PlayerOwnedCombined` exists for booleans where a
single shared answer is required (`CanSwitchSafely` is AND-folded, because a
block must not solidify if *anyone* is inside it). Nothing here is that shape.

The two `Patches.*` statics hold behaviour instances, not per-player values.
Behaviour instances are already handled — `BehaviourCloner` gives each player
its own copy of registered block behaviours — so they need no entry here.
Adding one would scope the wrong thing.

### How to verify it worked

1. Build and deploy per `docs/development-workflow.md` (workspace repo).
   Confirm Jump King is not running before copying, and verify by hash.
2. On startup the log line `Local Multiplayer gimmick compatibility: scoped N
   targets` should go from 31 to 32 with no new `unavailable targets` entry.
   A `type not found` for `MovementControl.Data.DataMomentumStop` means the
   mod is not subscribed on this machine, not that the entry is wrong.
3. On a map requiring Movement Control Blocks, put the two players on
   different screens across a momentum-stop block. Before the change, both
   players share whichever screen was written last; after, each keeps its own.

## Scope boundary

Do not add entries for mods not listed here without inspecting them the same
way first — a `ScopedType` entry naming a field that is not actually
per-player is worse than no entry, because it silently gives each player a
private copy of something the game expects to be shared. The inspection
recipe:

```powershell
# Preload so the mod's JumpKing-derived types resolve; without this,
# GetTypes() silently returns only the types that happened to load.
[Reflection.Assembly]::LoadFrom("<JumpKing>\JumpKing.exe")
$asm = [Reflection.Assembly]::LoadFrom("<mod>.dll")
$f = [Reflection.BindingFlags]
$flags = $f::Static -bor $f::Public -bor $f::NonPublic -bor $f::DeclaredOnly
$asm.GetTypes() | ForEach-Object {
  $_.GetFields($flags) | Where-Object { -not $_.IsLiteral -and -not $_.IsInitOnly }
}
```

Mutable statics are the candidates. `readonly` and `const` are not, since
nothing writes them per player.
