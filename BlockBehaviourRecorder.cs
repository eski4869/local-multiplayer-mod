using System;
using System.Collections.Generic;
using System.Reflection;
using JumpKing.API;
using JumpKing.Player;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Records every <c>IBlockBehaviour</c> a mod registers on player 1 during the
    /// base <c>[OnLevelStart]</c> dispatch, so the same set can be reproduced for
    /// the other players without running the mod's code again.
    ///
    /// The recording is the whole input to <see cref="BehaviourCloner"/>. Surveying
    /// the twelve block mods currently installed found 116 registrations, of which
    /// only 11 pass the player to the behaviour's constructor: the interface hands
    /// every method a <c>BehaviourContext</c> that already carries the player, so
    /// most behaviours never need to capture one. That is what makes reproducing a
    /// registration a matter of copying an object rather than re-running a hook.
    ///
    /// Only the dispatch window is recorded. <c>BodyComp</c> registers the base
    /// game's own ice, sand, water and snow behaviours from its constructor, and
    /// those already happen once per player - cloning them would be redundant at
    /// best.
    /// </summary>
    internal static class BlockBehaviourRecorder
    {
        internal sealed class Registration
        {
            public Type BlockType;
            public IBlockBehaviour Behaviour;

            /// <summary>
            /// The body it was registered on. Kept so the recorder needs no
            /// player context while the dispatch is running, which is what lets
            /// single player skip context creation entirely.
            /// </summary>
            public BodyComp Body;

            /// <summary>
            /// Taken from the behaviour's own type, not from a stack walk. The
            /// replay had to identify the calling mod to decide whether to re-run
            /// it; copying an object needs no such decision, so the walk - and its
            /// dependence on frames surviving inlining - is gone.
            /// </summary>
            public string ModName
            {
                get
                {
                    if (Behaviour == null)
                    {
                        return "?";
                    }

                    Assembly assembly = Behaviour.GetType().Assembly;
                    return assembly == null ? "?" : assembly.GetName().Name;
                }
            }
        }

        private static readonly List<Registration> Records = new List<Registration>();

        public static IList<Registration> All
        {
            get { return Records; }
        }

        public static int Count
        {
            get { return Records.Count; }
        }

        public static void Clear()
        {
            Records.Clear();
        }

        /// <summary>
        /// Called from the <c>RegisterBlockBehaviour</c> shim, before the base
        /// method runs.
        /// </summary>
        /// <param name="body">
        /// The body being registered on. Registrations on anything other than
        /// player 1 are ignored: the recording describes what player 1 received,
        /// and that is what the other players are given a copy of.
        /// </param>
        public static void Note(
            BodyComp body,
            Type blockType,
            IBlockBehaviour behaviour
        )
        {
            if (blockType == null || behaviour == null || body == null)
            {
                return;
            }

            // Recorded without consulting a player context. Whether this body is
            // the one that matters is decided later, when there is a context to
            // ask - a record must not be gated on the state at the time it is
            // taken, which is the mistake v1.2.2 shipped.
            Records.Add(new Registration
            {
                BlockType = blockType,
                Behaviour = behaviour,
                Body = body
            });
        }
    }
}
