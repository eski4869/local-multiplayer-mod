using System.Diagnostics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// The session's shared sense of time.
    ///
    /// **A frame counter is not a clock.** The game loop skips its whole update
    /// while the pause menu is open, so a client that pauses for ten seconds has
    /// simulated six hundred fewer frames than one that did not - and, if it takes
    /// its own tick count as the time, has no way to notice. That is the shape of
    /// the "pausing makes the lag worse every time" report against existing
    /// multiplayer mods: a queue filled in real time and drained one item per frame
    /// keeps whatever it accumulated, and keeps it permanently.
    ///
    /// So session time comes from a monotonic real clock and the frame number is
    /// derived from it. <see cref="Gap"/> is then the honest answer to "how far
    /// behind is this client", which is what a catch-up can act on.
    ///
    /// <see cref="Stopwatch"/> rather than <c>DateTime</c> on purpose: the wall
    /// clock can step sideways for daylight saving or an NTP correction, and a
    /// clock that jumps backwards would report a negative gap and desynchronise
    /// every client that trusted it.
    /// </summary>
    internal sealed class NetplayClock
    {
        /// <summary>
        /// Jump King runs a fixed step. This is the conversion between session time
        /// and frame numbers, and both ends must agree on it.
        /// </summary>
        public const int FramesPerSecond = 60;

        private readonly Stopwatch _elapsed = new Stopwatch();
        private long _simulatedFrames;

        public bool IsRunning
        {
            get { return _elapsed.IsRunning; }
        }

        /// <summary>Frames the session has actually simulated.</summary>
        public long SimulatedFrame
        {
            get { return _simulatedFrames; }
        }

        /// <summary>
        /// The frame the session should be on, from real time alone. Keeps
        /// advancing while the game is paused, which is the entire point.
        /// </summary>
        public long SessionFrame
        {
            get
            {
                return _elapsed.ElapsedMilliseconds * FramesPerSecond / 1000L;
            }
        }

        /// <summary>
        /// How many frames behind real time this client is. Zero in the ordinary
        /// case; large after a pause, a stall, or a long level load.
        /// </summary>
        public long Gap
        {
            get
            {
                long gap = SessionFrame - _simulatedFrames;
                return gap < 0 ? 0 : gap;
            }
        }

        public void Start()
        {
            _simulatedFrames = 0;
            _elapsed.Reset();
            _elapsed.Start();
        }

        public void Stop()
        {
            _elapsed.Stop();
        }

        /// <summary>Records that the simulation advanced one frame.</summary>
        public void NoteSimulatedFrame()
        {
            _simulatedFrames++;
        }

        /// <summary>
        /// Abandons a gap too large to be worth simulating, and treats the current
        /// moment as caught up.
        ///
        /// Used when the gap cannot be closed by simulating - a ten second pause is
        /// six hundred frames, and replaying those through every block behaviour
        /// inside one frame is not something to attempt. Skipping is the honest
        /// alternative to pretending the frames happened, and the caller decides
        /// what to do about the players it just moved.
        /// </summary>
        public void SkipTo(long frame)
        {
            if (frame > _simulatedFrames)
            {
                _simulatedFrames = frame;
            }
        }
    }
}
