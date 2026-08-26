using System;
using System.Collections.Generic;
using System.Reflection;
using BehaviorTree;
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

        public long Frame { get; private set; }

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

        public static PlayerSnapshot Capture(PlayerContext context, long frame)
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

            AddBehaviourTree(player, roots);
            AddOwnedObjects(roots);
            return roots;
        }

        /// <summary>
        /// Every node of the player's behaviour tree, and the manager over it.
        ///
        /// The tree is where "what is this player doing" actually lives. Its nodes
        /// keep running state - which child a sequence is part-way through, whether
        /// a selector is still running, the manager's own last result and tick -
        /// and none of it was being captured. A rollback restored the charge timer
        /// and left the tree believing it was somewhere else, which is a
        /// disagreement no later frame can resolve.
        ///
        /// Walked rather than listed, so a node type nobody thought of is covered
        /// anyway. It is bounded: the tree is built once at construction, and its
        /// nodes reference each other and the player, all of which are roots
        /// already.
        /// </summary>
        private static void AddBehaviourTree(PlayerEntity player, List<object> roots)
        {
            object comp = ReadStateField(player, "m_bt");
            if (comp == null)
            {
                return;
            }

            FieldInfo treeField = AccessTools.Field(comp.GetType(), "m_behavior_tree");
            object manager = treeField == null ? null : treeField.GetValue(comp);
            if (manager == null)
            {
                return;
            }

            if (!roots.Contains(manager))
            {
                roots.Add(manager);
            }

            FieldInfo rootField = AccessTools.Field(manager.GetType(), "m_root_node");
            var node = rootField == null ? null : rootField.GetValue(manager) as IBTnode;
            AddNode(node, roots, 0);
        }

        private static void AddNode(IBTnode node, List<object> roots, int depth)
        {
            // The tree is shallow by construction; the limit only stops a cycle
            // from becoming a hang.
            if (node == null || depth > 32 || roots.Contains(node))
            {
                return;
            }

            roots.Add(node);

            IBTnode[] related;
            try
            {
                related = node.GetRelatedNodes();
            }
            catch
            {
                return;
            }

            if (related == null)
            {
                return;
            }

            for (int i = 0; i < related.Length; i++)
            {
                AddNode(related[i], roots, depth + 1);
            }
        }

        /// <summary>
        /// Data holders the roots own outright, added as roots themselves.
        ///
        /// A root's own fields are captured by value, but an object it points at is
        /// not - the reference is kept and its contents are left alone, because
        /// following references reaches the level and every block in it. That is
        /// right for a collision query and wrong for a private scratch buffer.
        ///
        /// Recognised by shape rather than by a list of field names: a type whose
        /// every field is a value type or an array of value types cannot reach
        /// anything else, so capturing it is bounded by construction. That covers
        /// <c>JumpState</c>'s four-frame input buffer, which decides the direction
        /// of a jump when the key was released just before takeoff - and which,
        /// left out, made exactly one thing wrong: a left or right jump after a
        /// rollback.
        /// </summary>
        private static void AddOwnedObjects(List<object> roots)
        {
            var found = new List<object>();

            for (int i = 0; i < roots.Count; i++)
            {
                object root = roots[i];
                FieldInfo[] fields = StateSnapshot.FieldsOf(root.GetType());

                for (int f = 0; f < fields.Length; f++)
                {
                    if (fields[f].FieldType.IsValueType ||
                        fields[f].FieldType == typeof(string))
                    {
                        continue;
                    }

                    object value;
                    try
                    {
                        value = fields[f].GetValue(root);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value == null || value is Array ||
                        roots.Contains(value) || found.Contains(value) ||
                        !IsSelfContained(value.GetType()))
                    {
                        continue;
                    }

                    found.Add(value);
                }
            }

            roots.AddRange(found);
        }

        /// <summary>
        /// True when nothing of this type can reach an object outside itself.
        /// </summary>
        private static bool IsSelfContained(Type type)
        {
            bool cached;
            if (SelfContainedCache.TryGetValue(type, out cached))
            {
                return cached;
            }

            // Assumed false while being examined, so a type that refers to itself
            // settles rather than recursing.
            SelfContainedCache[type] = false;

            bool result = true;
            FieldInfo[] fields = StateSnapshot.FieldsOf(type);
            for (int i = 0; i < fields.Length && result; i++)
            {
                Type fieldType = fields[i].FieldType;
                if (fieldType.IsValueType || fieldType == typeof(string))
                {
                    continue;
                }

                if (fieldType.IsArray)
                {
                    Type element = fieldType.GetElementType();
                    result = element != null &&
                        (element.IsValueType || element == typeof(string));
                    continue;
                }

                result = false;
            }

            SelfContainedCache[type] = result;
            return result;
        }

        private static readonly Dictionary<Type, bool> SelfContainedCache =
            new Dictionary<Type, bool>();

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
