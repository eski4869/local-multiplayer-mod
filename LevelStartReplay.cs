using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using JumpKing.Mods;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Runs a mod's <c>[OnLevelStart]</c> hook once per player - but only for the
    /// mods that need it.
    ///
    /// Why replay at all: <c>IBlockBehaviour</c> is registered per <c>BodyComp</c>,
    /// and every gimmick block mod written for this game does
    /// <c>Find&lt;PlayerEntity&gt;()</c> then <c>RegisterBlockBehaviour</c> on that
    /// one body. Additional players get nothing and walk straight through the
    /// gimmick. Cloning the registered behaviours cannot work: their constructors
    /// take arguments that cannot be reconstructed from outside - an
    /// <c>ICollisionQuery</c>, a settings object, the <c>PlayerEntity</c> itself.
    /// Re-running the producer against each player makes the mod build a genuine
    /// instance with its own genuine arguments.
    ///
    /// Why only some mods: <c>[OnLevelStart]</c> is not a per-player hook. Mods
    /// use it for process-wide work too, and replaying that is actively harmful.
    /// Surveying the installed mods found real breakage - one stores the found
    /// player's body in a static that drives its HUD, so a blanket replay would
    /// leave it pointing at the last player instead of the first.
    ///
    /// So a mod qualifies only by demonstrating it is a block mod: it registered
    /// an <c>IBlockBehaviour</c> during the normal dispatch. Everything else runs
    /// exactly once, as it does without this mod installed.
    /// </summary>
    internal static class LevelStartReplay
    {
        /// <summary>
        /// Set for every pass after the first. Consumer mods can read this through
        /// <c>LocalMultiplayerApi.IsSecondaryInitPass</c> to skip process-wide work.
        /// </summary>
        public static bool SuppressEntityRegistration;

        /// <summary>
        /// True while the base game is running its own <c>[OnLevelStart]</c>
        /// dispatch, which is the window in which a mod can qualify for replay.
        /// </summary>
        public static bool IsBaseDispatch;

        private static readonly HashSet<Assembly> BlockBehaviourMods =
            new HashSet<Assembly>();
        private static HashSet<Assembly> _modAssemblies;
        private static bool _isSecondaryPass;
        private static bool _replaying;

        public static bool IsSecondaryPass
        {
            get { return _isSecondaryPass; }
        }

        public static void BeginBaseDispatch()
        {
            BlockBehaviourMods.Clear();
            _modAssemblies = null;
            IsBaseDispatch = true;
        }

        public static void EndBaseDispatch()
        {
            IsBaseDispatch = false;
        }

        /// <summary>
        /// Called from the <c>RegisterBlockBehaviour</c> shim. Walks back to the
        /// mod that made the call and marks it as replayable.
        /// </summary>
        public static void NoteBlockBehaviourRegistration()
        {
            if (!IsBaseDispatch)
            {
                return;
            }

            Assembly caller = FindCallingModAssembly();
            if (caller != null)
            {
                BlockBehaviourMods.Add(caller);
            }
        }

        /// <param name="contexts">
        /// Only the players that still need a pass. Player 1 is never included:
        /// the base dispatch already served it.
        /// </param>
        public static void Run(IList<PlayerContext> contexts)
        {
            if (_replaying || contexts == null || contexts.Count == 0)
            {
                return;
            }

            List<ModAssembly> mods = GetReplayableMods();
            if (mods.Count == 0)
            {
                return;
            }

            _replaying = true;
            _isSecondaryPass = true;
            SuppressEntityRegistration = true;

            try
            {
                for (int i = 0; i < contexts.Count; i++)
                {
                    PlayerContext context = contexts[i];
                    if (context == null || !context.IsAlive || context.IsPrimary)
                    {
                        continue;
                    }

                    using (PlayerScope.Enter(context, true))
                    {
                        InvokeAll(mods, context.Number);
                    }
                }
            }
            finally
            {
                SuppressEntityRegistration = false;
                _isSecondaryPass = false;
                _replaying = false;
            }
        }

        private static void InvokeAll(List<ModAssembly> mods, int playerNumber)
        {
            for (int i = 0; i < mods.Count; i++)
            {
                ModAssembly mod = mods[i];
                List<MethodInfo> methods = mod.OnLevelStartMethods;

                // Snapshot per mod rather than per pass, so one mod's globals are
                // back in place before the next mod runs.
                ReplayIsolation isolation = ReplayIsolation.Capture(mod.Assembly);
                try
                {
                    for (int j = 0; j < methods.Count; j++)
                    {
                        try
                        {
                            methods[j].Invoke(null, null);
                        }
                        catch (Exception ex)
                        {
                            // Match the base ModLoader contract: one failing mod
                            // must not stop the rest.
                            JumpKing.Program.crashLog.AddErrorMessage(
                                "Local Multiplayer level start replay failed for '" +
                                mod.ModName + "' on player " + playerNumber + ": " +
                                Unwrap(ex).Message
                            );
                            break;
                        }
                    }
                }
                finally
                {
                    // Registration landed on this player's BodyComp, which is an
                    // instance field and survives the rollback. Everything global
                    // goes back to what player 1's pass left behind.
                    isolation.Restore();
                }
            }
        }

        private static List<ModAssembly> GetReplayableMods()
        {
            var result = new List<ModAssembly>();
            ModLoader loader = ModLoader.Instance;
            List<ModAssembly> loaded = loader == null ? null : loader.LoadedMods;
            if (loaded == null)
            {
                return result;
            }

            Assembly self = Assembly.GetExecutingAssembly();
            for (int i = 0; i < loaded.Count; i++)
            {
                ModAssembly mod = loaded[i];
                if (mod == null || mod.Assembly == self ||
                    mod.OnLevelStartMethods == null ||
                    mod.OnLevelStartMethods.Count == 0)
                {
                    continue;
                }

                if (!ModEntry.ShouldReplayMod(
                    mod.ModName,
                    BlockBehaviourMods.Contains(mod.Assembly)
                ))
                {
                    continue;
                }

                result.Add(mod);
            }

            return result;
        }

        private static Assembly FindCallingModAssembly()
        {
            HashSet<Assembly> modAssemblies = GetModAssemblies();
            if (modAssemblies.Count == 0)
            {
                return null;
            }

            var trace = new StackTrace(false);
            int frames = trace.FrameCount;
            for (int i = 0; i < frames; i++)
            {
                MethodBase method = trace.GetFrame(i).GetMethod();
                Type declaring = method == null ? null : method.DeclaringType;
                Assembly assembly = declaring == null ? null : declaring.Assembly;
                if (assembly != null && modAssemblies.Contains(assembly))
                {
                    return assembly;
                }
            }

            return null;
        }

        private static HashSet<Assembly> GetModAssemblies()
        {
            if (_modAssemblies != null)
            {
                return _modAssemblies;
            }

            _modAssemblies = new HashSet<Assembly>();
            ModLoader loader = ModLoader.Instance;
            List<ModAssembly> loaded = loader == null ? null : loader.LoadedMods;
            if (loaded == null)
            {
                return _modAssemblies;
            }

            Assembly self = Assembly.GetExecutingAssembly();
            for (int i = 0; i < loaded.Count; i++)
            {
                if (loaded[i] != null && loaded[i].Assembly != self)
                {
                    _modAssemblies.Add(loaded[i].Assembly);
                }
            }

            return _modAssemblies;
        }

        private static Exception Unwrap(Exception ex)
        {
            var invocation = ex as TargetInvocationException;
            return invocation != null && invocation.InnerException != null ?
                invocation.InnerException : ex;
        }
    }
}
