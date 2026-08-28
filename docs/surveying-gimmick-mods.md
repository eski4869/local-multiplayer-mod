# Surveying gimmick mods

How to find out which third-party mods need per-player scoping, and which
need nothing — without guessing, and without reading source that may not
exist.

The method has two halves, and they answer different questions. Doing only
one of them is how effort lands in the wrong place.

| Question | Answered by |
| --- | --- |
| Which mods do maps actually use? | The Workshop, per map |
| Does a given mod hold per-player state? | Its assembly, by reflection |

A mod can be required by three quarters of popular maps and still need
nothing from this project. That is not hypothetical — it is what
`JumpKingPlus blocks` turned out to be.

## Half one: which mods do maps require

**Browse the Level category, not the Mod category.** The Mod category tells
you what exists. It does not tell you what matters, because an
installed-but-unused mod changes nothing — the same reason a mod-list
mismatch is a bad thing to refuse a netplay session over.

Steam publishes each Level's declared dependencies on the item's own page.

```
https://steamcommunity.com/workshop/browse/?appid=1061090
  &browsesort=trend&section=readytouseitems&requiredtags[]=Level
```

Item ids only exist in the DOM — the page text renders titles without hrefs.
Collect them with:

```js
[...document.querySelectorAll('a[href*="filedetails"]')]
  .map(a => [a.href.match(/id=(\d+)/)?.[1], a.textContent.trim()])
  .filter(([id, t]) => id && t)
```

Then, on each item page:

```js
[...document.querySelectorAll('.requiredItem')].map(e => e.textContent.trim())
```

**Steam rate-limits after roughly fifteen to twenty consecutive item pages.**
When it does, pages return an error document — and an error page and a map
with no dependencies produce the *same* empty array from that selector. Record
those as unknown. Writing them down as "no dependencies" manufactures data,
and the resulting table will be quietly wrong in a way nothing later catches.

## Half two: does the mod hold per-player state

**The signal is mutable static fields.** A mod written for one player can keep
"the player's" state in a static safely, because there is only ever one. With
two players that single box is asked to hold two values, and whoever writes it
last decides for everybody — the order-dependence symptom described in
`docs/gimmick-diagnosis.md` (workspace repo).

`readonly` and `const` fields are not candidates: nothing writes them per
player. Instance fields on behaviour objects are not candidates either, since
`BehaviourCloner` already gives each player its own copy.

```powershell
$JK = "C:\Program Files (x86)\Steam\steamapps\common\Jump King"

# Preload first. Without this, a mod's JumpKing-derived types fail to load and
# GetTypes() returns only the ones that happened to resolve - silently. The
# first pass of this survey missed every block behaviour that way and nearly
# concluded a mod was stateless when its types simply had not loaded.
[void][Reflection.Assembly]::LoadFrom("$JK\JumpKing.exe")
[void][Reflection.Assembly]::LoadFrom("$JK\MonoGame.Framework.dll")
[void][Reflection.Assembly]::LoadFrom("$JK\Content\JKMods\0Harmony.dll")

$asm = [Reflection.Assembly]::LoadFrom("<path to mod>.dll")
try { $types = $asm.GetTypes() }
catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | ? { $_ } }

$b = [Reflection.BindingFlags]
$flags = $b::Static -bor $b::Public -bor $b::NonPublic -bor $b::DeclaredOnly

foreach ($t in $types) {
  if ($t.Name -like "<>c*") { continue }   # compiler-generated closures
  $mutable = @($t.GetFields($flags) | ? { -not $_.IsLiteral -and -not $_.IsInitOnly })
  if ($mutable.Count) {
    "--- $($t.FullName)"
    $mutable | % { "      $($_.FieldType.Name)  $($_.Name)" }
  }
}
```

**Check the loaded type count against what you expect.** If a block mod
reports three types and none of them are blocks, the preload did not work.
Do not read a short list as "this mod is simple."

> Avoid `$IF` and similar as variable names in PowerShell — they collide with
> language keywords and produce confusing binding errors rather than a clean
> failure.

## Half three: deciding what each field is

Finding a mutable static is not the end. Each one needs a judgement that no
tool can make, because the answer is about what the game *means*, not what
the code says:

| Kind | Treatment | Example |
| --- | --- | --- |
| A value each player has independently | `PlayerOwned` — fully separated | `DataMomentumStop.Screen`, `DataAuto.HasSwitched` |
| A boolean where one shared answer is required | `PlayerOwnedCombined` — AND-folded across players | `CanSwitchSafely` — a block must not solidify if *anyone* is inside it |
| Level-load bookkeeping or configuration | **Leave alone** | `FactoryTrap.LastUsedMapId`, `JumpKingPlusBlockFactory.Flags` |
| An asset handle, sound, or texture | **Leave alone** | `JumpKingPlusEntry.WarpTransition` |

**Scoping the wrong field is worse than scoping nothing.** Giving each player
a private copy of something the game expects to be shared breaks the thing it
was trying to fix, and the failure looks like a gimmick bug rather than a
configuration mistake.

Type and name alone cannot decide this — `HasSwitched` and `CanSwitchSafely`
are both `bool`, on the same class, and take opposite treatments. When the
name is not conclusive, read what writes the field before adding an entry, or
leave it out and note it as unverified.

## Where the results live

- `gimmick-map-coverage.md` — the current table: which mods, which maps, what
  is covered
- `gimmick-coverage-plan.md` — what to add next and the exact code to add
- `third-party-compatibility.md` — how the scoping mechanism itself works
- `docs/workshop-ecosystem-index.md` (workspace repo) — the wider creator and
  ecosystem survey these came out of
