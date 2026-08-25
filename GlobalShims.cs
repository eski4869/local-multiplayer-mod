using System;
using System.Collections.Generic;
using System.Reflection;
using EntityComponent;
using HarmonyLib;
using JumpKing.API;
using JumpKing.MiscEntities.WorldItems;
using JumpKing.MiscEntities.WorldItems.Inventory;
using JumpKing.Player;
using JumpKing.Player.Skins;
using JumpKing.SaveThread;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Reads the base game's global skin list without going through
    /// <c>SkinManager.IsWearingSkin</c>, so the shim below cannot recurse.
    /// </summary>
    internal static class GlobalItemState
    {
        private static readonly Type SkinManagerType = AccessTools.TypeByName(
            "JumpKing.Player.Skins.SkinManager"
        );
        private static readonly FieldInfo AppliedSkinsField =
            SkinManagerType == null ? null : AccessTools.Field(
                SkinManagerType,
                "m_applied_skins"
            );

        public static readonly MethodInfo IsWearingSkinMethod =
            SkinManagerType == null ? null : AccessTools.Method(
                SkinManagerType,
                "IsWearingSkin"
            );

        public static bool IsWearingSkinUnpatched(Items item)
        {
            if (AppliedSkinsField == null)
            {
                return false;
            }

            var skins = AppliedSkinsField.GetValue(null) as List<Skin>;
            if (skins == null)
            {
                return false;
            }

            for (int i = 0; i < skins.Count; i++)
            {
                if (skins[i].item == item)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Redirects <c>SkinManager.IsWearingSkin</c> to the scoped player's loadout.
    ///
    /// This single patch covers every consumer in the base game -
    /// <c>Walk</c>, <c>WalkAnim</c>, <c>IsOnGround</c>, <c>FailState</c>, and the
    /// <c>IsWearingSkin</c> behaviour-tree node that gates the Giant Boots stomp -
    /// plus any mod that asks the same question.
    /// </summary>
    internal static class SkinManagerIsWearingSkinPatch
    {
        public static bool Prefix(Items __0, ref bool __result)
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null)
            {
                return true;
            }

            bool equipped;
            if (!context.ItemsInternal.TryGetOverride(__0, out equipped))
            {
                return true;
            }

            __result = equipped;
            return false;
        }
    }

    /// <summary>
    /// Redirects <c>InventoryManager.HasItemEnabled</c> to the scoped player's
    /// loadout. This is what <c>IceBlockBehaviour</c> and <c>SnowBlockBehaviour</c>
    /// consult for the Snake Ring.
    /// </summary>
    internal static class InventoryHasItemEnabledPatch
    {
        public static bool Prefix(Items __0, ref bool __result)
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null)
            {
                return true;
            }

            bool equipped;
            if (!context.ItemsInternal.TryGetOverride(__0, out equipped))
            {
                return true;
            }

            __result = equipped && InventoryManager.HasItem(__0);
            return false;
        }
    }

    /// <summary>
    /// <c>SaveLube</c> is internal, so its player-position property is reached
    /// reflectively. Resolved once at load, not per frame.
    /// </summary>
    internal static class SaveLubeAccess
    {
        private static readonly PropertyInfo PlayerPositionProperty =
            AccessTools.Property(
                AccessTools.TypeByName("JumpKing.SaveThread.SaveLube"),
                "PlayerPosition"
            );

        public static MethodInfo Getter
        {
            get
            {
                return PlayerPositionProperty == null ? null :
                    PlayerPositionProperty.GetGetMethod(true);
            }
        }

        public static MethodInfo Setter
        {
            get
            {
                return PlayerPositionProperty == null ? null :
                    PlayerPositionProperty.GetSetMethod(true);
            }
        }

        public static SaveState GetPlayerPosition()
        {
            if (PlayerPositionProperty == null)
            {
                return default(SaveState);
            }

            return (SaveState)PlayerPositionProperty.GetValue(null, null);
        }
    }

    /// <summary>
    /// Keeps additional players out of the real save slot. Player 1 still owns
    /// <c>SaveLube.PlayerPosition</c>; the others read and write their own copy,
    /// which means <c>PlayerEntity.Update</c> can run normally for every player
    /// instead of being skipped.
    /// </summary>
    internal static class SaveLubePlayerPositionGetPatch
    {
        public static bool Prefix(ref SaveState __result)
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null || context.IsPrimary)
            {
                return true;
            }

            __result = context.SaveState;
            return false;
        }
    }

    internal static class SaveLubePlayerPositionSetPatch
    {
        public static bool Prefix(SaveState value)
        {
            PlayerContext context = PlayerScope.Current;
            if (context == null || context.IsPrimary)
            {
                return true;
            }

            context.SaveState = value;
            return false;
        }
    }

    /// <summary>
    /// Makes <c>EntityManager.Find&lt;PlayerEntity&gt;()</c> return the scoped
    /// player. Almost every block mod uses that call in <c>[OnLevelStart]</c> to
    /// find "the player" and register behaviours on its <c>BodyComp</c>; with this
    /// in place the level-start replay hands each mod a different player and it
    /// builds real behaviours for each one.
    ///
    /// Deliberately gated on <see cref="PlayerScope.RedirectPrimaryPlayer" />:
    /// entity order also drives update and draw order, so it must not be disturbed
    /// during the regular frame walk.
    /// </summary>
    internal static class EntityManagerEntitiesPatch
    {
        private static readonly IReadOnlyList<Entity> Empty =
            new List<Entity>().AsReadOnly();

        public static void Postfix(ref IReadOnlyList<Entity> __result)
        {
            // The IForeground loop that closes JumpGame.Draw is inline, so it
            // cannot be patched on its own. Emptying the list is what keeps it
            // from running a second time in the screen-space pass, after the
            // per-view passes already drew those overlays with the right camera.
            if (MultiplayerSplitRenderer.IsScreenSpacePass)
            {
                __result = Empty;
                return;
            }

            if (!PlayerScope.RedirectPrimaryPlayer || __result == null)
            {
                return;
            }

            PlayerContext context = PlayerScope.Current;
            PlayerEntity player = context == null ? null : context.Player;
            if (player == null || __result.Count == 0 ||
                ReferenceEquals(__result[0], player))
            {
                return;
            }

            var reordered = new List<Entity>(__result.Count);
            reordered.Add(player);
            for (int i = 0; i < __result.Count; i++)
            {
                if (!ReferenceEquals(__result[i], player))
                {
                    reordered.Add(__result[i]);
                }
            }

            __result = reordered.AsReadOnly();
        }
    }

    /// <summary>
    /// Records every block behaviour registered during the level-start dispatch,
    /// which is what the additional players are given a copy of.
    ///
    /// The generic <c>RegisterBlockBehaviour&lt;T&gt;</c> overload forwards to
    /// this one, so patching here catches both call styles.
    ///
    /// Recorded even in single player. Whether a recording is needed is not known
    /// until later - switching to multiplayer from the pause menu sets players up
    /// mid-run, long after this dispatch is over - and a record must not be gated
    /// on the state at the time it is taken.
    /// </summary>
    internal static class BodyCompRegisterBlockBehaviourPatch
    {
        public static void Prefix(
            BodyComp __instance,
            Type blockType,
            IBlockBehaviour blockBehaviour
        )
        {
            BlockBehaviourRecorder.Note(__instance, blockType, blockBehaviour);
        }
    }

    /// <summary>
    /// Suppresses the world half of <c>JumpGame.Draw</c> during the split
    /// renderer's screen-space pass.
    ///
    /// <c>JumpGame.Draw</c> has a clean seam: it draws the world (background,
    /// screen, entities, screen foreground), then everything after that is
    /// screen-space UI at fixed coordinates. The split renderer draws the world
    /// once per view into its own target, composites them, and then needs the UI
    /// exactly once at full size. Replaying the whole method per view is what put
    /// a pause menu and a timer inside each half.
    ///
    /// Skipping these four leaves that second pass drawing UI only.
    /// </summary>
    internal static class WorldDrawSuppressionPatch
    {
        public static bool Prefix()
        {
            return !MultiplayerSplitRenderer.IsScreenSpacePass;
        }
    }

}
