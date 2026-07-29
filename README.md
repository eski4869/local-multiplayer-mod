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

Commands carrying a `user` can be assigned to players with exact names,
prefix patterns, `*`, or an initial range such as `[a-m]*`. A user may match
more than one player. Commands without a user target Player 1 in Single Player
mode and are ignored in multiplayer modes.

## Mod API

`LocalMultiplayerApi` provides a small optional integration surface:

- `GetApiVersion()`
- `IsActive()`
- `GetPlayerCount()`
- `ResolvePlayers(string user)`
- `ResolvePlayerMask(string user)`
- `GetPlayer(int playerNumber)`
- `SubmitInput(int playerNumber, InputComponent.State held, InputComponent.State pressed)`

`ResolvePlayers` is the preferred integration boundary. It resolves user routing
to concrete `PlayerEntity` instances, so consumer mods can apply one operation to
every returned player without branching on Player 1 through Player 4. Consumer
mods should resolve this optional API once at startup. If this mod is absent,
they should use their normal single-player resolver.

## Requirements

- Jump King
- Harmony (`0Harmony.dll`)

## Tests

```text
dotnet test LocalMultiplayerMod.Tests/LocalMultiplayerMod.Tests.csproj
```
