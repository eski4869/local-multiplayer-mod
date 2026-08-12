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

| Source | Damage | |
| --- | --- | --- |
| Landing on another player's head | 20 | Flattens them into the game's splat |
| A splat landing | 12 | The game's own splat, so terminal velocity only |

Five clean stomps take a round. When someone runs out, the winner is announced
for three seconds and everybody is healed to full — positions are left alone, so
nobody loses the height they climbed.

The two damage sources are meant to pull against each other. Getting above an
opponent is the only way to land on them, so height is what wins fights; but a
stomp that misses leaves you falling from that same height, and the splat at the
bottom costs you health as well. A landed stomp cancels the fall, so the attack
also saves you from the fall it created.

A stomp puts the victim into the base game's splat: flattened, held there until
they press something, and with their horizontal velocity zeroed. Being caught
mid-jump therefore also costs the rest of that jump. The attacker rebounds at
half the speed they arrived with, capped — the same restitution the game gives a
wall, so dropping further bounces higher and stepping off a ledge barely hops.

Nothing checks how fast the attacker was moving sideways, only that they were
coming down and their feet reached the top of the other player. Diving in at an
angle from a height is a stomp; arriving level is not.

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
behaviours on its `BodyComp`, so they only ever set up player 1. Every player is
therefore created before that dispatch runs, and the dispatch is then replayed
once per additional player with the lookup pointed at it. The mod builds real
behaviours for each player with its own real constructor arguments.

Entity registration and draw-order shuffling are suppressed during a replayed
pass, which separates a mod's per-player setup (block behaviours, player
components) from its process-wide setup (singleton drawing and animation
entities). A mod that needs to tell the difference itself can read
`IsSecondaryInitPass()`.

## Writing a multiplayer-friendly mod

Consumer mods should stay multiplayer-agnostic: resolve a user to one
`PlayerEntity` and otherwise write ordinary single-player code.

The one rule that matters: **per-player state must not live in a static field.**
A static is a cross-player bug the moment a second player exists - two players
standing on the same lever will overwrite each other's "has already toggled"
flag every frame. World state that all players genuinely share, such as whether
a switch is currently on, is fine as a static.

Use `GetPlayerState` / `SetPlayerState` for anything owned by one player.

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
| `IsSecondaryInitPass()` | Inside a replayed `[OnLevelStart]` |
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
- A third-party mod that stores per-player state in a static still mixes players
  up. That can only be fixed in the mod itself, by moving the state onto
  `GetPlayerState` / `SetPlayerState`.
- A mod that does process-wide work in `[OnLevelStart]` other than creating
  entities - loading sounds, registering block factories - repeats that work once
  per player. Statics it assigns are rolled back after each replayed pass, so the
  effect is repeated work rather than corrupted state.

## Requirements

- Jump King
- Harmony (`0Harmony.dll`)

## Tests

```text
dotnet test LocalMultiplayerMod.Tests/LocalMultiplayerMod.Tests.csproj
```
