# Local Multiplayer Mod

Adds local two-player and four-player modes to Jump King. Each player runs in
the same game process with an independent player entity, camera view, and input
channel.

## Modes

Use `Local Multiplayer` in the main or pause menu to select:

- Single Player
- 2 Players
- 2 Players (Compact), with two 240 x 180 views and black bars above and below
- 4 Players

Changing from Single Player to a multiplayer mode reloads the user-routing
rules from `eski4869.LocalMultiplayerMod.Settings.xml`.

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

### Known limits

- A third-party mod that stores per-player state in a static still mixes players
  up. That can only be fixed in the mod itself, by moving the state onto
  `GetPlayerState` / `SetPlayerState`.
- A mod that does process-wide work in `[OnLevelStart]` other than creating
  entities - loading sounds, registering block factories - repeats that work once
  per player.

## Requirements

- Jump King
- Harmony (`0Harmony.dll`)

## Tests

```text
dotnet test LocalMultiplayerMod.Tests/LocalMultiplayerMod.Tests.csproj
```
