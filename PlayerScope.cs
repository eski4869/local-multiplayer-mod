using System;
using System.Reflection;
using HarmonyLib;
using JumpKing;
using Microsoft.Xna.Framework;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// The ambient "player currently being processed".
    ///
    /// Entering a scope installs that player's camera into <see cref="Camera" />,
    /// which is what the base game consults for collision
    /// (<c>LevelManager.CheckCollision</c>, <c>GetCollisionInfo</c>,
    /// <c>IsInWater</c>), for <c>LevelManager.CurrentScreen</c>, and for every
    /// draw transform. Leaving the scope writes the camera back into the context
    /// and restores the previous one, so scopes nest.
    ///
    /// Player 1 enters a scope too. Consumer mods therefore see one uniform
    /// contract no matter which player is being processed.
    /// </summary>
    internal static class PlayerScope
    {
        private const int MaximumDepth = 8;

        private static readonly FieldInfo CameraScreenField = AccessTools.Field(
            typeof(Camera),
            "_current_screen"
        );

        private static readonly PlayerContext[] Stack =
            new PlayerContext[MaximumDepth];
        private static readonly int[] SavedScreens = new int[MaximumDepth];
        private static readonly Vector2[] SavedOffsets = new Vector2[MaximumDepth];
        private static readonly bool[] WriteBack = new bool[MaximumDepth];
        private static int _depth;

        /// <summary>
        /// While set, <c>EntityManager.Entities</c> is reordered so that
        /// <c>Find&lt;PlayerEntity&gt;()</c> returns the scoped player. Only enabled
        /// during the per-player level-start replay and explicit API scopes, never
        /// during the regular update or draw walk, where entity order is meaningful.
        /// </summary>
        public static bool RedirectPrimaryPlayer;

        public static PlayerContext Current
        {
            get { return _depth > 0 ? Stack[_depth - 1] : null; }
        }

        public static bool IsAvailable
        {
            get { return CameraScreenField != null; }
        }

        /// <summary>
        /// Drops any scope left behind by an exception thrown inside a patched
        /// method, where the Harmony postfix never runs. Called once per frame at
        /// a point where the depth is known to be zero, so a single bad frame
        /// cannot leave the camera stuck for the rest of the run.
        /// </summary>
        public static void ResetIfLeaked()
        {
            if (_depth == 0 && !RedirectPrimaryPlayer)
            {
                return;
            }

            for (int i = 0; i < MaximumDepth; i++)
            {
                Stack[i] = null;
            }

            _depth = 0;
            RedirectPrimaryPlayer = false;
        }

        /// <summary>
        /// Snapshots the global camera and puts it back on dispose.
        ///
        /// <c>new PlayerEntity()</c> ends its constructor with
        /// <c>Camera.UpdateCamera(...)</c> at the position it loaded from the save
        /// file, so spawning an additional player would otherwise yank player 1's
        /// view to the save point.
        /// </summary>
        public static CameraGuard PreserveGlobalCamera()
        {
            return CameraScreenField == null ?
                new CameraGuard(false, 0, Vector2.Zero) :
                new CameraGuard(true, Camera.CurrentScreen, Camera.Offset);
        }

        internal struct CameraGuard : IDisposable
        {
            private bool _active;
            private readonly int _screen;
            private readonly Vector2 _offset;

            public CameraGuard(bool active, int screen, Vector2 offset)
            {
                _active = active;
                _screen = screen;
                _offset = offset;
            }

            public void Dispose()
            {
                if (!_active)
                {
                    return;
                }

                _active = false;
                CameraScreenField.SetValue(null, _screen);
                Camera.Offset = _offset;
            }
        }

        public static Scope Enter(PlayerContext context)
        {
            return Enter(context, false, true);
        }

        public static Scope Enter(PlayerContext context, bool redirectPrimaryPlayer)
        {
            return Enter(context, redirectPrimaryPlayer, true);
        }

        /// <param name="writeBack">
        /// When true, camera movement that happened inside the scope is stored
        /// back onto the context. Update passes want this (that is how
        /// <c>CameraFollowComp</c> drives the player's view); draw passes do not.
        /// </param>
        public static Scope Enter(
            PlayerContext context,
            bool redirectPrimaryPlayer,
            bool writeBack
        )
        {
            if (context == null || CameraScreenField == null ||
                _depth >= MaximumDepth)
            {
                return new Scope(false, false);
            }

            SavedScreens[_depth] = Camera.CurrentScreen;
            SavedOffsets[_depth] = Camera.Offset;
            WriteBack[_depth] = writeBack;
            Stack[_depth] = context;
            _depth++;

            if (!context.CameraSeeded)
            {
                context.Screen = Camera.CurrentScreen;
                context.Offset = Camera.Offset;
                context.CameraSeeded = true;
            }

            CameraScreenField.SetValue(null, context.Screen);
            Camera.Offset = context.Offset;

            bool restoreRedirect = RedirectPrimaryPlayer;
            if (redirectPrimaryPlayer)
            {
                RedirectPrimaryPlayer = true;
            }

            return new Scope(true, redirectPrimaryPlayer && !restoreRedirect);
        }

        private static void Exit(bool clearRedirect)
        {
            if (_depth <= 0)
            {
                return;
            }

            _depth--;
            PlayerContext context = Stack[_depth];
            Stack[_depth] = null;

            bool writeBack = WriteBack[_depth];
            if (context != null && writeBack)
            {
                // CameraFollowComp ran inside the scope and moved the global
                // camera; that movement belongs to this player.
                context.Screen = Camera.CurrentScreen;
                context.Offset = Camera.Offset;
            }

            // Player 1 *is* the global camera: everything outside a scope, the
            // split renderer's base view included, reads what its CameraFollowComp
            // left behind. Restoring here would freeze the view, so its update
            // scope commits to the global camera instead of restoring.
            bool commitToGlobal =
                context != null && writeBack && context.IsPrimary;

            if (!commitToGlobal)
            {
                CameraScreenField.SetValue(null, SavedScreens[_depth]);
                Camera.Offset = SavedOffsets[_depth];
            }

            if (clearRedirect)
            {
                RedirectPrimaryPlayer = false;
            }
        }

        internal struct Scope : IDisposable
        {
            private bool _entered;
            private readonly bool _clearRedirect;

            public Scope(bool entered, bool clearRedirect)
            {
                _entered = entered;
                _clearRedirect = clearRedirect;
            }

            public void Dispose()
            {
                if (!_entered)
                {
                    return;
                }

                _entered = false;
                Exit(_clearRedirect);
            }
        }
    }
}
