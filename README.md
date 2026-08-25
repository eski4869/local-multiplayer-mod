# Local Multiplayer Mod

Adds local two-player and four-player modes to Jump King. Each player runs in
the same game process with an independent player entity, camera view, and input
channel.

## Modes

Use `Local Multiplayer` in the main or pause menu to select:

- Single Player
- 2 Players, side by side with 240 x 360 views
- 2 Players (Compact), with two 240 x 180 views and black bars above and below
- 2 Players (Stacked), one above the other with 480 x 180 views, so each player
  keeps the full screen width. Suits a map whose route runs sideways, where half
  the width cuts off what the player needs to see
- 2 Players (Shared), no split at all: one full-size view on player 1's camera
  with the other player drawn into it
- 4 Players

`Shared` is for playing together in one picture rather than racing in two. Two
cameras are hard to read when the players are meant to be interacting, which is
what battle mode is for. The trade is that player 2 is only visible while inside
player 1's screen — they keep playing when they leave it, they just cannot be
seen. Collision, items and the win check all still resolve against each player's
own camera; only the drawing is shared.

Changing from Single Player to a multiplayer mode reloads the user-routing
rules from `eski4869.LocalMultiplayerMod.Settings.xml`.

## Battle mode

`Battle: On` in the same menu turns the climb into a fight. Each king carries a
health gauge above their head, and there are two ways to lose it.

**Damage is the energy of an impact** — the square of how fast you were falling,
as a fraction of terminal velocity. One law covers both ways health is lost: a
stomp hands the impact to whoever is underneath, a splat is the same impact
absorbed by the player who made it. A full-speed landing therefore costs exactly
what a full-speed stomp deals.

Both read the speed actually landed at, never an assumed one. A splat is
normally terminal velocity by definition — `FailState` only starts when
`LastVelocity.Y` equals `MAX_FALL` exactly — but the node sits behind the player
tree's `IsOnGround` guard, so a splatted player who leaves the ground has it
suspended mid-run and `ResumeRun` calls `Start` again on landing without
re-checking the speed. Charging terminal velocity there turned a half-pixel
shove into a full-speed fall, which is what once let a downed player be walked
to death. Reading the real speed makes that nudge worth the one point an impact
that small is worth, and leaves a genuine fall at the twenty it always was.

| Fall | Landing speed | Damage |
| --- | --- | --- |
| 1 block | 2.0 | 1 |
| 5 blocks | 4.5 | 4 |
| 10 blocks | 6.4 | 8 |
| 15 blocks | 7.9 | 12 |
| 24 blocks (terminal) | 10.0 | 20 |

Five clean maximum-speed hits take a round — the only number here chosen by
design rather than derived. When someone runs out, the winner is announced for
three seconds, then everybody is healed and returned to the level's own spawn
points, so a round is never won from the position the last one ended in.

Squared rather than linear is what settles the balance. Stepping off a ledge
onto someone is worth almost nothing, so trading pokes on level ground can never
win a round; getting above an opponent is the only thing that does. But a stomp
that misses leaves you falling from that same height, and the landing costs you
exactly what the hit would have been worth.

A stomp puts the victim into the base game's splat: flattened, held there until
they press something, and with their horizontal velocity zeroed. Being caught
mid-jump therefore also costs the rest of that jump.

The attacker rebounds with the same restitution the game gives a wall. Together
with the recovery window that follows a hit, that decides how long a bounce
chain runs: off a full-speed dive the first rebound stays airborne longer than
the victim's splat lasts, so a second stomp connects — and the one after it does
not. **A dive is worth exactly two hits**, and only from around fifteen blocks
up, which is the height that makes the first rebound long enough.

Nothing checks how fast the attacker was moving sideways, only that they were
coming down and their feet reached the top of the other player. Diving in at an
angle from a height is a stomp; arriving level is not.

### Where the numbers come from

Everything in the combat model is derived from constants the base game already
defines, so there is nothing to re-tune when one of them changes:

| | |
| --- | --- |
| Rebound off a head | `PlayerValues.BOUNCE` — a head returns what a wall does |
| Side collision restitution | `PlayerValues.BOUNCE` — the same event, so the same share |
| Recovery window after a hit | `SPLAT_TIME` — you cannot be hit again before the game lets you stand up |
| Head band | `MAX_FALL` — one frame of terminal velocity, the smallest band that never misses a stomp |
| Contact threshold | `WALK_SPEED` — below what a walking player brings, it is drift, not an impact |

Two numbers are not derived, and both are honest about it: **five hits per
round**, which is a design decision about round length, and the **lift a shove
gives a standing player**, which exists only because `Walk` rewrites
`Velocity.X` every frame a player is on the floor. A purely horizontal shove is
erased before it moves anyone, so something has to lift them and no law says by
how much. The lift scales with the impulse actually delivered — a running jump
into someone knocks them further off their feet than a walk does — and the
constant only fixes where that scale is anchored.

### Props

A map can place two kinds of object in the arena, in the `BattleProps` section
of its `local_multiplayer.xml`:

```xml
<BattleProps>
  <Heal>
    <Position><X>228</X><Y>72</Y></Position>
    <Amount>25</Amount>
    <RespawnSeconds>20</RespawnSeconds>
  </Heal>
  <Walker>
    <From><X>300</X><Y>352</Y></From>
    <To><X>450</X><Y>352</Y></To>
    <Speed>0.4</Speed>
    <Damage>8</Damage>
    <RespawnSeconds>10</RespawnSeconds>
  </Walker>
</BattleProps>
```

**Heal** restores health on contact and comes back after its respawn time. It
declines to be taken by someone already at full health, so it is still there for
whoever needs it. Worth placing somewhere that has to be climbed to: wanting it
is then a reason to take on the fall risk, which is the same trade the rest of
the fight runs on.

**Walker** patrols between two points. Touching its sides costs health; landing
on its head kills it until it respawns, using exactly the same head band that
decides a stomp between two players — so nothing new has to be learned to deal
with one. Slow on purpose, since the players sharing the arena are being driven
through chat.

The walker is also the answer to a stalemate. Two players who refuse to commit
can hold their ground indefinitely against each other, but not against something
that keeps arriving.

Hazard damage goes through the same recovery window as a stomp, so a walker
cannot drain someone who is already down, and cannot stack with a stomp landing
in the same instant.

### Side contact

Players who meet at the same height trade horizontal velocity, as two equal
masses would, keeping 70% of the exchange so a collision settles instead of
firing both away harder than they arrived. It costs no health directly — the
damage comes from where the shove leaves you.

Only the horizontal is traded. Swapping the vertical too would let a falling
player hand off their fall and hang in the air.

A player standing still is lifted off the ground by the equivalent of a
two-frame jump charge. That is not decoration: `Walk` rewrites `Velocity.X`
every frame a player is on the floor, so a purely sideways shove is erased
before it moves anyone.

### Details that follow

- The attacker must be moving downward *and* have actually changed position, so
  a pair left overlapping across a pause cannot trade hits while nothing moves.
- The victim is safe for 24 frames after a hit, and a pair cannot trade another
  shove for 12. Players have no collision with each other, so without this one
  landing would register every frame the boxes stayed overlapped.
- Only players on the same screen touch. Screens sit 360 apart in world space,
  so two players in rooms a map joins left-to-right through a teleport are
  physically about one body height apart — close enough to overlap without being
  anywhere near each other on screen. The cost is that contact across a screen
  boundary does not register either.
- Reaching the ending still ends the race for everyone, battle mode or not.

The setting is independent of the player count, so it can be left on while
dropping back to Single Player, where it does nothing.

## User routing

Each mode has `DefaultRoutes` for prefix patterns, `*`, or an initial range
such as `[a-m]*`. `UserOverrides` stores exact user-to-player assignments.
Overrides take priority over default routes; otherwise the first matching
default route from Player 1 through Player 4 is selected. Commands without a
user target Player 1 in Single Player mode and are ignored in multiplayer
modes.

```xml
<FourPlayerMode>
  <DefaultRoutes>
    <Player1Users>[a-f]*</Player1Users>
    <Player2Users>[g-m]*</Player2Users>
    <Player3Users>[n-s]*</Player3Users>
    <Player4Users>[t-z]*</Player4Users>
  </DefaultRoutes>
  <UserOverrides>
    <User name="z" player="1" />
  </UserOverrides>
</FourPlayerMode>
```

The `local_multiplayer` Broker target can persist an exact override for the
currently selected mode:

```text
http://127.0.0.1:8081/command?target=local_multiplayer&user=alice&command=p1
http://127.0.0.1:8081/command?target=local_multiplayer&user=alice&command=p2
```

`p1` through `p4` are accepted only when that player exists in the selected
mode. Assigning an existing user updates its single override. Default routes
and overrides belonging to other modes are unchanged.

## How it works

Jump King keeps most per-player state in one-player globals. Rather than patch
each one where it breaks, this mod introduces a single concept and routes the
globals through it.

**Player context.** One per player, player 1 included, owning what used to be
global: the camera, the item loadout, the save state, and a free-form slot bag
for consumer mods.

**Player scope.** The ambient "player currently being processed". Entering a
scope installs that player's camera into `Camera`, which is what
`LevelManager.CheckCollision`, `GetCollisionInfo` and `IsInWater` resolve
against, and what every draw transform reads. Leaving it restores the previous
one, so scopes nest. Player 1 enters a scope too, so consumer mods see one
uniform contract no matter who is being processed.

**Global shims.** Small Harmony patches that redirect a one-player global to the
scoped player: `SkinManager.IsWearingSkin`, `InventoryManager.HasItemEnabled`,
`SaveLube.PlayerPosition`, and `EntityManager.Entities`. Two of those are why
Giant Boots and the Snake Ring reach every player - they cover every consumer in
the base game at once, including `Walk`, `IsOnGround`, `FailState`,
`IceBlockBehaviour` and `SnowBlockBehaviour`.

**Split rendering.** `JumpGame.Draw` has a seam: it draws the world, then
everything after that is screen-space UI at fixed coordinates. Only the world is
drawn per view; the views are composited, and then the UI is drawn once at full
size over them. Drawing the whole game per view is what used to put a pause menu
and a run timer inside each half.

`IForeground` stays in the per-view pass. The interface is a layer, not a
coordinate space, and the overlays that use it are world-anchored. If your
overlay is anchored to a player, check `IsPlayerInCurrentView` before drawing, or
it will appear in views that are not showing that player.

**Per-player level start.** Block mods find "the player" with
`EntityManager.Find<PlayerEntity>()` inside `[OnLevelStart]` and register their
behaviours on its `BodyComp`, so they only ever set up player 1. Every
registration made during that dispatch is recorded, and each additional player
is then given a copy of every recorded behaviour, with any player-typed field
rebound to that player.

**No mod code is re-run.** An earlier version instead replayed each block mod's
`[OnLevelStart]` once per player and rolled back the globals it touched. That
worked, but its correctness was unbounded — what a hook can reach cannot be
enumerated, so every mod that did something process-wide in its level-start hook
was a new rollback case to find. Copying an object has nothing to roll back,
needs no entity or draw-order suppression, and does not depend on mod load
order. `IsSecondaryInitPass()` remains for compatibility and now always returns
false: there is no secondary pass.

### Mods that cannot be asked to change

Per-player setup gives each player its own behaviours, but it cannot help a mod
that keeps per-player state in a static — or that caches one `PlayerEntity` and
consults it from patches running for every player. Those are corrected at run
time from `GimmickStateCompat`, without modifying the mod: the patterns, and the
rules for adding one, are in
[third-party compatibility](docs/third-party-compatibility.md).

### When something is wrong and the cause is not visible

The mod ships with diagnostic probes, all switched off. Turn one on in the
settings file, reproduce, and read `crashlog.log` — no rebuild. What each one
answers, and the rules for writing another, are in [probes](docs/probes.md).

## Writing a multiplayer-friendly mod

Consumer mods should stay multiplayer-agnostic: resolve a user to one
`PlayerEntity` and otherwise write ordinary single-player code.

The one rule that matters: **per-player state must not live in a static field.**
A static is a cross-player bug the moment a second player exists - two players
standing on the same lever will overwrite each other's "has already toggled"
flag every frame. World state that all players genuinely share, such as whether
a switch is currently on, is fine as a static.

Use `GetPlayerState` / `SetPlayerState` for anything owned by one player.

## Writing a multiplayer-friendly map

Everything a map can tell this mod goes in **`local_multiplayer.xml`**, a file
of its own next to `level_settings.xml`:

```xml
<?xml version="1.0"?>
<LocalMultiplayerLevel xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <StartPositions>
    <Player2>
      <Position><X>151</X><Y>327</Y></Position>
      <Velocity xsi:nil="true" />
    </Player2>
  </StartPositions>
  <BattleProps>
    <Heal>
      <Position><X>228</X><Y>72</Y></Position>
      <Amount>25</Amount>
      <RespawnSeconds>20</RespawnSeconds>
    </Heal>
  </BattleProps>
</LocalMultiplayerLevel>
```

**Do not put any of this in `level_settings.xml`.** Worldsmith round-trips that
file through its own settings type when it saves, and silently drops every
element it does not recognise — so anything written there works right up until
the author next opens the editor, which is the worst possible way for it to
disappear. A separate file is never rewritten.

**The game reads the level's `bin` folder, not the project folder.** Worldsmith
compiles the project into `bin` and the game is pointed at that, so a file it
does not know about has to be copied there as well — a `local_multiplayer.xml`
sitting only beside `level.png` is never read. Put it in both, or in `bin` and
let the project copy be the source you edit.

A map with no such file behaves exactly as it did before, so this is invisible
to every existing level.

### Start positions

By default every additional player spawns exactly where player 1 does — the
right behaviour for a map built as a single climb that multiplayer just races.
`Player2` / `Player3` / `Player4` override that individually; player 1 keeps
using the base game's own `StartData`.

`Velocity` is optional, same as on `StartData` itself. Any player left without
an entry falls back to player 1's spawn, so a two-player arena only needs
`Player2`, and adding four-player support later means adding two more elements
rather than restructuring anything.

A spawn on a different screen from player 1 is fine: the camera for it is
computed from the position the same way the base game's own teleports do it.

## Mod API

`LocalMultiplayerApi` is the integration boundary. Everything is public static
with base-game or primitive parameter types, so consumers can bind it
reflectively and fall back to single player when this mod is absent.

| Member | Purpose |
| --- | --- |
| `GetApiVersion()` | Currently 4 |
| `IsActive()` | Multiplayer is running right now |
| `GetPlayerCount()` | Live player count |
| `ResolvePlayer(user)` | Chat user to zero or one `PlayerEntity` |
| `GetPlayer(number)` / `GetPlayerNumber(player)` | Index lookups |
| `GetCurrentPlayer()` | The scoped player, or null outside a scope |
| `RunAsPlayer(player, action)` | Run an action with that player scoped |
| `IsPlayerInCurrentView(player)` | Draw only what belongs to the active view |
| `IsSecondaryInitPass()` | Always false; kept for compatibility |
| `IsItemEquipped` / `SetItemEquipped` / `ToggleItem` | Per-player item state |
| `GetPlayerState` / `SetPlayerState` | Per-player storage |

`ResolvePlayer` resolves user routing to zero or one concrete `PlayerEntity`, so
consumers do not branch on Player 1 through Player 4 or handle routing masks.

### Items

Giant Boots and the Snake Ring are per player. The base game has no per-player
channel for them at all: `GameLoop` reads the toggle straight off the one
physical pad and applies it to a global skin list. Each player carries its own
override, seeded from the global state when the player is created; every other
item stays global so cosmetics behave as they do in single player. A mod that
knows which user sent a toggle should route it through `ToggleItem`.

In single player no override is ever set, the shims never intercept, and the
game behaves exactly as it does without this mod installed.

Equipment is also drawn per player. The base game keeps one sprite layer list
shared by everyone, so an additional player would otherwise wear player 1's
equipment; the layer list is rebuilt from that player's own items instead. Only
additional players reach that code - player 1 keeps drawing the game's own
sprites, which is already correct because the global equip state is the one the
local pad toggles.

### Known limits

- Some SwitchBlocks switch types do not respond for additional players. Warp,
  one-way and the other gimmick blocks do. Under investigation.
- A third-party mod that stores per-player state in a static mixes players up.
  Several such mods are corrected from here without changing them - see
  [third-party compatibility](docs/third-party-compatibility.md) - but each one
  has to be diagnosed and added by hand, so an undiagnosed mod is still affected.
  A mod that can be changed should move the state onto
  `GetPlayerState` / `SetPlayerState` instead.
- State a third-party mod genuinely shares between players, where two players
  change what it means rather than corrupting it, cannot be resolved by
  redirection at all.
- A behaviour that captures something player-specific in a field the copier
  cannot recognise as player-typed keeps player 1's copy. The known cases are
  rebound by name; a new one would need adding.

## Requirements

- Jump King
- Harmony (`0Harmony.dll`)

## Tests

```text
dotnet test LocalMultiplayerMod.Tests/LocalMultiplayerMod.Tests.csproj
```
