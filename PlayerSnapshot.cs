using System;
using System.Collections.Generic;
using System.Reflection;
using EntityComponent;
using HarmonyLib;
using JumpKing.Player;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// One player's complete simulation state, captured so it can be put back.
    ///
    /// This is the rollback contract from the netplay design: "return to the state
    /// at the confirmed frame and recompute". Everything that decides where a player
    /// ends up has to be in here, and nothing that merely decides how they look
    /// needs to be.
    ///
    /// **The base game's own SaveState is not enough.** `PlayerEntity.GetSaveState`
    /// records position, velocity, direction, timestamp and whether the player is
    /// splatted - and `ApplySaveState` restores only position, velocity, direction
    /// and splat. It carries no charge progress. Charge is exactly the state that
    /// makes this game hard to net: 36 frames maps to the full jump, and one frame
    /// is about one block of height, which is the whole reason lockstep was
    /// rejected. Restoring a player without their charge would silently discard the
    /// only thing rollback exists to protect.
    ///
    /// **The camera is simulation, not presentation.** `LevelManager.GetCollisionInfo`
    /// searches the tracked screen plus or minus one and nothing else, so a player
    /// restored with the wrong screen finds no ground anywhere. That was measured
    /// once already, as a player falling ninety-nine screens.
    /// </summary>
    internal sealed class PlayerSnapshot
    {
        private readonly List<object> _roots = new List<object>();
        private readonly List<object[]> _values = new List<object[]>();

        private int _screen;
        private Vector2 _offset;
        private bool _cameraSeeded;
        private Dictionary<object, object> _scoped;

        public int Frame { get; private set; }

        public int RootCount
        {
            get { return _roots.Count; }
        }

        /// <summary>
        /// The objects that make up a player's own state.
        ///
        /// Bounded on purpose. A player's references reach the whole world through
        /// `BodyComp`'s collision query, so this list is what a snapshot means -
        /// following references instead would try to capture the map.
        ///
        /// Components come from the entity itself rather than a hand-written list,
        /// so a component another mod adds to a player is covered without this file
        /// knowing about it. The behaviour-tree states are named explicitly because
        /// they are plain fields on <c>PlayerEntity</c> rather than components, and
        /// they are where charge and splat live.
        /// </summary>
        private static readonly string[] BehaviourTreeStateFields =
        {
            "m_bt",
            "m_jump_state",
            "m_fail_state",
            "m_is_on_ground_state"
        };

        private static readonly Dictionary<string, FieldInfo> StateFieldCache =
            new Dictionary<string, FieldInfo>();

        public static PlayerSnapshot Capture(PlayerContext context, int frame)
        {
            var snapshot = new PlayerSnapshot();
            if (context == null || !context.IsAlive)
            {
                return snapshot;
            }

            snapshot.Frame = frame;

            List<object> roots = CollectRoots(context.Player);
            for (int i = 0; i < roots.Count; i++)
            {
                snapshot._roots.Add(roots[i]);
                snapshot._values.Add(StateSnapshot.Capture(roots[i]));
            }

            snapshot._screen = context.Screen;
            snapshot._offset = context.Offset;
            snapshot._cameraSeeded = context.CameraSeeded;

            // Per-player gimmick state: the sand contact flags, the countdown
            // velocity, the scoped gravity direction. All of it decides what happens
            // next, so all of it has to come back with the rest.
            snapshot._scoped = new Dictionary<object, object>(
                context.State,
                ObjectCopier.ReferenceComparer.Instance
            );

            return snapshot;
        }

        /// <summary>
        /// Puts the captured values back into the objects they came from.
        /// </summary>
        /// <returns>
        /// False when the player has been rebuilt since the capture, which makes the
        /// snapshot describe objects that no longer exist.
        /// </returns>
        public bool Restore(PlayerContext context)
        {
            if (context == null || !context.IsAlive || _roots.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < _roots.Count; i++)
            {
                if (!StateSnapshot.Restore(_roots[i], _values[i]))
                {
                    return false;
                }
            }

            context.Screen = _screen;
            context.Offset = _offset;
            context.CameraSeeded = _cameraSeeded;

            if (_scoped != null)
            {
                context.State.Clear();
                foreach (KeyValuePair<object, object> entry in _scoped)
                {
                    context.State[entry.Key] = entry.Value;
                }
            }

            return true;
        }

        /// <summary>
        /// Reference-typed fields on the roots that no root covers.
        ///
        /// Read this when adding a mod to the supported set, not every frame. Most
        /// entries are correct - a sprite, a settings object, the collision query -
        /// and the ones that are not are mutable state that would survive a rollback
        /// unchanged. Reporting them is what turns that from a silent wrong answer
        /// into a decision somebody made.
        /// </summary>
        public IList<string> DescribeUncovered()
        {
            var notes = new List<string>();
            var roots = new HashSet<object>(ObjectCopier.ReferenceComparer.Instance);
            for (int i = 0; i < _roots.Count; i++)
            {
                roots.Add(_roots[i]);
            }

            for (int i = 0; i < _roots.Count; i++)
            {
                StateSnapshot.DescribeUncoveredReferences(_roots[i], roots, notes);
            }

            return notes;
        }

        private static List<object> CollectRoots(PlayerEntity player)
        {
            var roots = new List<object>();
            if (player == null)
            {
                return roots;
            }

            // Whatever is attached to this player, including components other mods
            // added. Asking the entity is what keeps this from being a list that
            // goes stale the first time somebody extends a player.
            IList<Component> components = player.GetComponents();
            if (components != null)
            {
                for (int i = 0; i < components.Count; i++)
                {
                    if (components[i] != null)
                    {
                        roots.Add(components[i]);
                    }
                }
            }

            for (int i = 0; i < BehaviourTreeStateFields.Length; i++)
            {
                object state = ReadStateField(player, BehaviourTreeStateFields[i]);
                if (state != null && !roots.Contains(state))
                {
                    roots.Add(state);
                }
            }

            // The entity's own fields carry the sprite direction and the save
            // timestamp, and it is the owner of everything above.
            roots.Add(player);
            return roots;
        }

        private static object ReadStateField(PlayerEntity player, string name)
        {
            FieldInfo field;
            if (!StateFieldCache.TryGetValue(name, out field))
            {
                field = AccessTools.Field(typeof(PlayerEntity), name);
                StateFieldCache[name] = field;
            }

            if (field == null)
            {
                return null;
            }

            try
            {
                return field.GetValue(player);
            }
            catch
            {
                return null;
            }
        }
    }
}
