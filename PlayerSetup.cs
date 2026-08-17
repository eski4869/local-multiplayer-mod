using System;
using System.Collections.Generic;
using JumpKing.API;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Gives the additional players the block behaviours player 1 received.
    ///
    /// Two mechanisms exist side by side while the second is being proven.
    ///
    /// <c>Replay</c> re-runs each block mod's <c>[OnLevelStart]</c> once per player
    /// and rolls back the globals it touched. It works, and it is what shipped, but
    /// its correctness is unbounded: rolling back everything a hook can reach
    /// cannot be enumerated, the rollback restores references rather than values,
    /// and the traversal order follows mod load order, which the loader does not
    /// sort.
    ///
    /// <c>Clone</c> runs no mod code at all. The behaviours player 1 was given are
    /// copied, their player-typed fields rebound, and the copies registered. There
    /// is nothing to roll back because nothing was re-run, no entity or draw-order
    /// suppression is needed for the same reason, and the result does not depend on
    /// load order.
    ///
    /// The default stays <c>Replay</c> until the manifests agree. Switching the
    /// default on a released mod before that comparison exists would be trading a
    /// known behaviour for an unmeasured one.
    /// </summary>
    internal static class PlayerSetup
    {
        public static void Run(IList<PlayerContext> contexts)
        {
            if (contexts == null || contexts.Count == 0)
            {
                return;
            }

            PlayerSetupMode mode = ModEntry.PlayerSetupMode;

            if (mode == PlayerSetupMode.Clone)
            {
                CloneForAll(contexts);
            }
            else
            {
                LevelStartReplay.Run(contexts);
            }

            if (ModEntry.WriteSetupManifest)
            {
                PlayerSetupManifest.Write(mode.ToString());
            }
        }

        private static void CloneForAll(IList<PlayerContext> contexts)
        {
            IList<BlockBehaviourRecorder.Registration> records =
                BlockBehaviourRecorder.All;

            if (records.Count == 0)
            {
                return;
            }

            for (int i = 0; i < contexts.Count; i++)
            {
                PlayerContext context = contexts[i];
                if (context == null || !context.IsAlive || context.IsPrimary)
                {
                    continue;
                }

                CloneFor(context, records);
            }
        }

        private static void CloneFor(
            PlayerContext context,
            IList<BlockBehaviourRecorder.Registration> records
        )
        {
            // One map for this player's whole set. A mod that registered the same
            // instance for several block types meant those blocks to share state,
            // and the copies have to share in the same shape.
            IDictionary<object, object> identityMap = BehaviourCloner.NewIdentityMap();

            PlayerContext primary = MultiplayerRuntime.GetContext(1);

            for (int i = 0; i < records.Count; i++)
            {
                BlockBehaviourRecorder.Registration record = records[i];

                // Which body mattered is decided here rather than while recording,
                // so the dispatch needs no player context and single player can
                // skip creating one.
                if (primary == null || record.Body != primary.Body)
                {
                    continue;
                }

                string problem;
                var clone = BehaviourCloner.Clone(
                    record.Behaviour,
                    context,
                    identityMap,
                    out problem
                ) as IBlockBehaviour;

                if (clone == null)
                {
                    Report(record, context, "could not be copied");
                    continue;
                }

                if (problem != null)
                {
                    Report(record, context, problem);
                }

                try
                {
                    context.Body.RegisterBlockBehaviour(record.BlockType, clone);
                }
                catch (Exception ex)
                {
                    Report(record, context, ex.Message);
                }
            }
        }

        private static void Report(
            BlockBehaviourRecorder.Registration record,
            PlayerContext context,
            string detail
        )
        {
            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer could not give player " + context.Number +
                " the '" + record.ModName + "' behaviour for " +
                record.BlockType.Name + ": " + detail
            );
        }

    }

    internal enum PlayerSetupMode
    {
        /// <summary>Re-run each block mod's level-start hook per player.</summary>
        Replay,

        /// <summary>Copy player 1's behaviours for each additional player.</summary>
        Clone
    }
}
