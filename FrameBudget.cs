using System.Diagnostics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Whether this machine is finishing its frames, reported once a second,
    /// whatever the mod happens to be doing.
    /// </summary>
    /// <remarks>
    /// **This exists to answer one question with one machine.** The frame timings
    /// were only reported during a netplay session, which meant the only way to ask
    /// whether a machine could hold sixty frames a second was to arrange two of
    /// them and a network - and then the answer arrived tangled up with everything
    /// the network was doing.
    ///
    /// The question that matters is simpler and separable: is this machine slow, or
    /// does this mod make it slow? Run it alone, run it with a second player
    /// locally, and compare. No peer, no packets, no netcode in the way.
    ///
    /// The game is fixed-timestep, so its own delta is a constant sixtieth of a
    /// second however badly the hardware is doing and can never answer this.
    /// MonoGame's <c>IsRunningSlowly</c> can: it means the update loop could not
    /// finish inside its budget and is catching up.
    /// </remarks>
    internal static class FrameBudget
    {
        /// <summary>
        /// Off unless asked for. It is a line a second on an unbuffered log, which
        /// is nothing, and a line a second nobody reads is still clutter.
        /// </summary>
        public static bool Enabled;

        private static long _lastTimestamp;
        private static long _frameBegan;
        private static double _milliseconds;
        private static double _workMilliseconds;
        private static int _frames;
        private static int _slowFrames;

        /// <summary>
        /// The end of a frame's work - after <c>Game1.Draw</c> has run.
        /// </summary>
        /// <remarks>
        /// **Measuring inside the frame rather than between frames is what
        /// separates being busy from waiting**, and it needs no opinion about which
        /// part is the graphics card. Everything the frame does is between these
        /// two stamps.
        ///
        /// Read against the frame period:
        ///
        /// - period near 16.7 and work well under it: the machine is idle most of
        ///   the frame and waiting for the display. Room to spare.
        /// - period and work both over 16.7 and close together: the machine is busy
        ///   for the whole frame and then some. This is the case that cannot be
        ///   fixed by sending fewer packets.
        ///
        /// The timings this replaced only had the period, which cannot tell those
        /// apart - and the per-component numbers beside it are all CPU-side, so
        /// they understate anything the graphics card is made to do.
        /// </remarks>
        public static void NoteFrameEnd()
        {
            if (!Enabled || _frameBegan == 0)
            {
                return;
            }

            _workMilliseconds +=
                (Stopwatch.GetTimestamp() - _frameBegan) * 1000.0 /
                Stopwatch.Frequency;
        }

        public static void Note(bool runningSlowly)
        {
            if (!Enabled)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            _frameBegan = now;

            if (_lastTimestamp > 0)
            {
                _milliseconds +=
                    (now - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;
                _frames++;
            }

            _lastTimestamp = now;

            if (runningSlowly)
            {
                _slowFrames++;
            }

            if (_frames < 60)
            {
                return;
            }

            // Once, and from here because this is the only path that runs whether
            // or not a session exists.
            SnapshotCost.MeasureOnce();

            JumpKing.Program.crashLog.AddErrorMessage(
                "frame budget: players=" + ModEntry.PlayerCount +
                " netplay=" + (ModEntry.Netplay.IsPlaying ? 1 : 0) +
                " frame_ms=" + (_milliseconds / _frames).ToString("F2") +
                " work_ms=" + (_workMilliseconds / _frames).ToString("F2") +
                " slow=" + _slowFrames +
                " sim_ms=" + PerFrame(FrameCost.SimulationMilliseconds) +
                " p2_ms=" + PerFrame(FrameCost.AdditionalPlayerMilliseconds) +
                " scope_ms=" + PerFrame(FrameCost.ScopeMilliseconds) +
                " draw_ms=" + PerFrame(FrameCost.DrawMilliseconds)
            );

            _milliseconds = 0;
            _workMilliseconds = 0;
            _frames = 0;
            _slowFrames = 0;

            // Only reset what this owns. During a session the netplay report clears
            // these on its own schedule; outside one nothing else would.
            if (!ModEntry.Netplay.IsPlaying)
            {
                FrameCost.Reset();
            }
        }

        private static string PerFrame(double total)
        {
            return _frames == 0 ? "0.00" : (total / _frames).ToString("F2");
        }
    }
}
