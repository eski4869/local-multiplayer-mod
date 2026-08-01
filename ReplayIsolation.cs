using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JumpKing.Player;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Rolls back the global state a replayed <c>[OnLevelStart]</c> touches, so a
    /// replay pass keeps only what it registered on that player's own body.
    ///
    /// This is what makes the replay safe rather than merely useful. A mod's
    /// level-start hook mixes per-player registration with process-wide setup in
    /// one method, and there is no way to run only half of it. Running the whole
    /// thing and then undoing the global half gets the same result.
    ///
    /// SwitchBlocks is the case that forced this. Its Jump setup does three
    /// global things the other seven setups do not:
    ///
    ///   SetupJump.EntityLogicJump = new EntityLogicJump(settings, player);
    ///   PlayerEntity.OnJumpCall = Delegate.Combine(OnJumpCall, JumpSwitchUnsafe);
    ///
    /// Without a rollback, a replay leaves the static pointing at a logic entity
    /// bound to the wrong player - one that is also deliberately unregistered, so
    /// it never updates - and subscribes the jump handler a second time, which
    /// toggles the switch twice per jump and cancels itself out. Its Cleanup only
    /// removes one subscriber, so the surplus accumulates across runs.
    ///
    /// Every other switch type keeps its logic entity in a local and never touches
    /// a global delegate, which is exactly why some switch blocks worked and the
    /// jump ones did not.
    /// </summary>
    internal sealed class ReplayIsolation
    {
        private static readonly FieldInfo OnJumpCallField = AccessTools.Field(
            typeof(PlayerEntity),
            "OnJumpCall"
        );
        private static readonly FieldInfo OnSplatCallField = AccessTools.Field(
            typeof(PlayerEntity),
            "OnSplatCall"
        );

        private readonly List<KeyValuePair<FieldInfo, object>> _values =
            new List<KeyValuePair<FieldInfo, object>>();

        /// <summary>
        /// Captures every writable static of the mod, plus the base game's two
        /// player callbacks, which are plain static delegate fields that mods add
        /// themselves to rather than C# events.
        /// </summary>
        public static ReplayIsolation Capture(Assembly assembly)
        {
            var snapshot = new ReplayIsolation();
            snapshot.CaptureField(OnJumpCallField);
            snapshot.CaptureField(OnSplatCallField);

            foreach (Type type in GetTypes(assembly))
            {
                if (type == null || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Static | BindingFlags.DeclaredOnly
                    );
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    snapshot.CaptureField(fields[i]);
                }
            }

            return snapshot;
        }

        public void Restore()
        {
            for (int i = 0; i < _values.Count; i++)
            {
                try
                {
                    _values[i].Key.SetValue(null, _values[i].Value);
                }
                catch
                {
                    // A static that cannot be written back is not worth aborting
                    // the rest of the rollback for.
                }
            }
        }

        private void CaptureField(FieldInfo field)
        {
            // Constants have no storage, and readonly statics cannot be written
            // back, so capturing either would be pointless.
            if (field == null || field.IsLiteral || field.IsInitOnly)
            {
                return;
            }

            try
            {
                _values.Add(
                    new KeyValuePair<FieldInfo, object>(field, field.GetValue(null))
                );
            }
            catch
            {
                // Reading a static runs the type initializer, which can throw for
                // a type this map never uses. Skipping it is correct: a type that
                // failed to initialize has no state to roll back.
            }
        }

        private static IEnumerable<Type> GetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Partially loadable assembly: work with whatever resolved.
                return ex.Types ?? new Type[0];
            }
            catch
            {
                return new Type[0];
            }
        }
    }
}
