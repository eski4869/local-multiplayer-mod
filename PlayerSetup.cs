using System;
using System.Collections.Generic;
using JumpKing.API;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Gives the additional players the block behaviours player 1 received, by
    /// copying them.
    ///
    /// No mod code runs. The behaviours player 1 was given are copied, their
    /// player-typed fields rebound, and the copies registered.
    ///
    /// The alternative this replaced was re-running each block mod's
    /// <c>[OnLevelStart]</c> once per player and rolling back the globals it
    /// touched. That worked, but its correctness was unbounded: what a hook can
    /// reach cannot be enumerated, so every mod that did something global in its
    /// level-start hook was a new rollback case to discover - a liability against
    /// mods not yet written. It also restored references rather than values, and
    /// depended on mod load order, which the loader does not sort. Copying an
    /// object has none of those properties.
    /// </summary>
    internal static class PlayerSetup
    {
        public static void Run(IList<PlayerContext> contexts)
        {
            if (contexts == null || contexts.Count == 0)
            {
                return;
            }

            CloneForAll(contexts);

            if (ModEntry.WriteSetupManifest)
            {
                PlayerSetupManifest.Write("Clone");
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
}
