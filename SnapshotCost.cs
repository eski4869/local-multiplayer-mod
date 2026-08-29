using System.Diagnostics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// What capturing and restoring a player costs on this machine, measured once,
    /// without a network.
    /// </summary>
    /// <remarks>
    /// **The expensive half of a correction has nothing to do with the network.**
    /// Restoring a player is a reflective write over every field of every root; how
    /// long that takes depends on the machine and the shape of the object graph,
    /// and on nothing else. Yet it could only be observed during a session, on
    /// whichever machine happened to be the one predicting - so answering "is this
    /// slow, and why" needed two computers, a lobby, and a peer running ahead.
    ///
    /// It needs one computer. Capture a player and put the same values straight
    /// back: the world is unchanged, because they are the values it already has,
    /// and the timing is the real thing rather than a model of it.
    ///
    /// The failed-write count is what separates a cost that can be removed from one
    /// that cannot. The restore loop catches per field, and a thrown exception
    /// costs far more than the write it stands in for - so dozens of them would
    /// explain a restore two orders slower than the capture, and would be fixable
    /// by deciding once per type which fields can be written. Reflection simply
    /// being slow would not be fixable the same way, and would call for a different
    /// answer or none.
    /// </remarks>
    internal static class SnapshotCost
    {
        private static bool _measured;

        public static void Reset()
        {
            _measured = false;
        }

        /// <summary>
        /// Measures once, the first time there is a player to measure.
        /// </summary>
        public static void MeasureOnce()
        {
            if (_measured)
            {
                return;
            }

            PlayerContext context = MultiplayerRuntime.GetContext(1);
            if (context == null || !context.IsAlive)
            {
                return;
            }

            _measured = true;

            int failedBefore = StateSnapshot.FailedWrites;

            var captureTimer = Stopwatch.StartNew();
            PlayerSnapshot snapshot = PlayerSnapshot.Capture(context, 0);
            captureTimer.Stop();

            // Putting back exactly what was just taken. Nothing in the world moves;
            // every value written is the value already there.
            var restoreTimer = Stopwatch.StartNew();
            bool restored = snapshot.Restore(context);
            restoreTimer.Stop();

            NetplayLog.Write(
                "snapshot cost: capture_ms=" +
                captureTimer.Elapsed.TotalMilliseconds.ToString("F2") +
                " restore_ms=" +
                restoreTimer.Elapsed.TotalMilliseconds.ToString("F2") +
                " write_fail=" + (StateSnapshot.FailedWrites - failedBefore) +
                " restored=" + (restored ? 1 : 0)
            );

            StateSnapshot.FailedWrites = failedBefore;
        }
    }
}
