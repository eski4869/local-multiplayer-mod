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

## Mod API

`LocalMultiplayerApi` provides a small optional integration surface:

- `GetApiVersion()`
- `IsActive()`
- `ResolvePlayer(string user)`
- `IsPlayerInCurrentView(PlayerEntity player)`

`ResolvePlayer` is the integration boundary. It resolves user routing to zero or
one concrete `PlayerEntity`, so consumers do not branch on Player 1 through
Player 4 or handle routing masks. Consumer mods resolve this optional API once
at startup. `IsPlayerInCurrentView`
lets drawing mods render only effects belonging to the active split-screen view.
If this mod is absent, consumers use their normal single-player resolver.

## Requirements

- Jump King
- Harmony (`0Harmony.dll`)

## Tests

```text
dotnet test LocalMultiplayerMod.Tests/LocalMultiplayerMod.Tests.csproj
```
