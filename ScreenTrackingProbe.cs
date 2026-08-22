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
    }
}
