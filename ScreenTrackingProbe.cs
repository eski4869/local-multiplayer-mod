using System;
using JumpKing.Player;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Checks whether <c>PlayerContext.Screen</c> - the value collision resolves
    /// against for a player who is not currently installed as the global camera -
    /// ever diverges from the screen the player's own position actually falls on.
    ///
    /// The base game derives a screen from position with one formula everywhere:
    /// <c>-(int)Floor(Position.Y / 360)</c>. <c>Camera.CurrentScreen</c> is
    /// supposed to track that continuously through <c>CameraFollowComp</c>, but
    /// that logic is guarded by velocity sign and was written for a single
    /// always-live camera. If our per-player scope ever hands it a stale
    /// baseline, the tracked screen can lag the real one, and
    /// <c>GetCollisionInfo</c> only looks at the tracked screen plus or minus
    /// one - so a large enough lag means no ground is found at all, which reads
    /// as physics not running.
    ///
    /// Temporary. Delete once the question is answered.
    /// </summary>
    internal static class ScreenTrackingProbe
    {
        private static readonly int[] LastLogged = new int[5];

        public static void Sample(int playerNumber, PlayerContext context)
        {
            if (!ModEntry.Diagnostics.ScreenTracking ||
                context == null || context.Body == null)
            {
                return;
            }

            int real = -(int)Math.Floor(context.Body.Position.Y / 360f);
            int tracked = context.Screen;
            int delta = tracked - real;

            if (delta == LastLogged[playerNumber])
            {
                return;
            }

            LastLogged[playerNumber] = delta;
            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer screen tracking: player " + playerNumber +
                " tracked=" + tracked + " real=" + real + " delta=" + delta +
                " onGround=" + context.Body.IsOnGround +
                " y=" + context.Body.Position.Y
            );
        }

        /// <summary>
        /// Records that <see cref="PlayerScope" /> overrode a tracked screen that
        /// had fallen too far from the body's own position.
        ///
        /// Every one of these means something moved a player from outside its own
        /// update, which is the event this probe exists to catch. Reported here
        /// rather than left to <see cref="Sample" />, because the correction
        /// happens first and restores the drift to zero - so by the time the
        /// sample runs there is nothing left to see.
        /// </summary>
        public static void NoteReconciled(PlayerContext context, int real)
        {
            if (!ModEntry.Diagnostics.ScreenTracking || context == null ||
                context.Body == null)
            {
                return;
            }

            // The corrected value is what the next sample will read, so recording
            // it keeps the two logs telling one continuous story.
            LastLogged[context.Number] = 0;

            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer screen reconciled: player " + context.Number +
                " tracked=" + context.Screen + " real=" + real +
                " drift=" + (context.Screen - real) +
                " onGround=" + context.Body.IsOnGround +
                " y=" + context.Body.Position.Y
            );
        }
    }
}
