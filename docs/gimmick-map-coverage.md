# Gimmick coverage against the maps that actually use it

Which gimmick mods this mod scopes for two players, and which popular maps
would be affected. Sampled 2026-08-28 from the Steam Workshop's Level
category sorted by popularity.

Two separate questions live here, and conflating them is how effort ends up
in the wrong place:

- **Does a map require this mod?** Answered by Steam, on each Level's page.
- **Does this mod hold per-player state?** Answered by inspecting its
  assembly. Most do not. See `gimmick-coverage-plan.md` for that inspection
  and what it found.

A mod can be required by every map and still need nothing from us.

## Coverage by mod

| Gimmick mod | Maps requiring it | Holds per-player state? | Covered |
| --- | --- | --- | --- |
| JumpKingPlus blocks | **15** | No — level config and a sound handle only | n/a |
| Expansion Blocks | 7 | Yes | ✅ `JumpKing_Expansion_Blocks.ModEntry` |
| Forced Slope Blocks | 6 | No — zero mutable statics | n/a |
| Movement Control Blocks | 4 | **Yes — `DataMomentumStop.Screen`** | ❌ **not yet** |
| Trap Sand Blocks | 3 | Probably not — level-load bookkeeping | verify first |
| Disable Screen Events | 3 | not inspected | — |
| Less Game SFX | 3 | No — audio | n/a |
| Switch Blocks | 2 | Yes | ✅ 8 `SwitchBlocks.*` types |
| More Text Options | 2 | No — presentation | n/a |
| More Layering Options | 2 | No — presentation | n/a |
| Anti Blocks | 2 | not inspected | — |
| Conveyor Block | 2 | not inspected | — |
| UpsideDownCore (Vertigo Core) | 0 in this sample | Yes | ✅ `UpsideDownCore.Controller` + resync |
| ExpansionBlock2 | 1 | Yes | ✅ (same scoping as Expansion Blocks) |
| HitboxResizer, Multi Ending Level Name, More Ending Options, Wind Particles Flip | 1 each | not inspected | — |

**`UpsideDownCore` did not appear in any sampled map**, despite being the
gimmick that has consumed the most integration work here (a property scope
plus a per-frame cache resync). It may be used by maps outside the popular
sample, or mainly by unlisted ones. Worth knowing before spending more on it.

## Per-map dependencies

Twenty of the top thirty. The other ten are listed below as unknown rather
than as having no dependencies — an error page and an empty list look
identical in the DOM.

| Map | Required gimmick mods |
| --- | --- |
| Babe Nemo | Forced Slope, Expansion, JumpKingPlus |
| Babe of Nayuta enjoy edition | Conveyor, JumpKingPlus, Expansion, ExpansionBlock2, Forced Slope, **Movement Control**, Trap Sand |
| Boots Babe Ring 3rd | JumpKingPlus, Expansion, Anti, Forced Slope, **Movement Control**, Multi Ending Level Name, Disable Screen Events |
| Baba is You | Expansion, JumpKingPlus |
| Babe of Ascension Legacy | JumpKingPlus |
| Tyrant Babe Nerfed | *(none declared)* |
| Mortal Babe | More Text, **Switch Blocks**, Wind Particles Flip, **Movement Control**, JumpKingPlus, Forced Slope, Expansion, Less Game SFX |
| Babe of Utopia | JumpKingPlus |
| Babe of Ascension | JumpKingPlus |
| Sisters of Space | JumpKingPlus |
| Super Jing World | JumpKingPlus, HitboxResizer |
| Babe of Fate | Expansion, More Layering, JumpKingPlus |
| Back to the Babe | *(none declared)* |
| Babe of the Heavens | JumpKingPlus |
| Babe of Nayuta.S | JumpKingPlus |
| Cybernetic Utopia | JumpKingPlus |
| Babe of Memories | JumpKingPlus |
| Babe of Dimension | JumpKingPlus |
| Babe of Exile | Anti, Conveyor, Expansion, Forced Slope, JumpKingPlus, More Text, **Switch Blocks**, Trap Sand, Less Game SFX, Disable Screen Events |
| Project Onyx | JumpKingPlus, Less Game SFX, More Ending Options, Forced Slope, More Layering, Trap Sand, Disable Screen Events, Expansion, **Movement Control** |

**Not retrieved** (four hit an error page, six were never requested before the
crawl was stopped): Immortal Babe, Waterfall Babe, Babe of Dimension+, Babe of
Nayuta, Rick King, MinusOne, Joe's Trial of the Babe, Babe of Inferno, Babe of
Transcension, Hamster Cage.

## What this says about priorities

**Three of twenty maps require Movement Control Blocks, and it is the only
uncovered mod confirmed to hold per-player state.** Those three — Babe of
Nayuta enjoy edition, Boots Babe Ring 3rd, Mortal Babe, plus Project Onyx —
are where two-player sessions would show the order-dependence symptom today.

Everything else in the top of the list needs nothing, which is the useful
result: the coverage gap is narrow, not wide.

## Re-running this

On any Level's Workshop page:

```js
[...document.querySelectorAll('.requiredItem')].map(e => e.textContent.trim())
```

Steam rate-limits after roughly fifteen to twenty consecutive item pages.
Space the requests out, and treat an error page as missing data rather than as
an empty result — the two are indistinguishable from the DOM, and recording
one as the other invents a fact.
