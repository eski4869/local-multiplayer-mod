namespace LocalMultiplayerMod
{
    /// <summary>
    /// Decides what the remote player did on a frame, and remembers the answer so a
    /// wrong one can be found later.
    ///
    /// Two timelines, and the difference between them is the whole mechanism. One
    /// holds what actually arrived over the wire. The other holds what the
    /// simulation was actually given - a guess or the real thing - because "was the
    /// guess wrong" cannot be answered from the real inputs alone.
    ///
    /// Separated from the session so the harness drives this exact code rather than
    /// a second copy of it written to agree with it. A copy would pass its tests and
    /// prove nothing about the game.
    /// </summary>
    internal sealed class RemoteInputResolver
    {
        private readonly InputTimeline _arrived;
        private readonly InputTimeline _used;

        public RemoteInputResolver(InputTimeline arrived, InputTimeline used)
        {
            _arrived = arrived;
            _used = used;
        }

        /// <summary>Frames compared since the counters were last read.</summary>
        public int Compared { get; private set; }

        /// <summary>Skipped because the real input has still not arrived.</summary>
        public int SkippedNoActual { get; private set; }

        /// <summary>Skipped because no frame has consumed that input yet.</summary>
        public int SkippedNoUsed { get; private set; }

        public void ResetCounters()
        {
            Compared = 0;
            SkippedNoActual = 0;
            SkippedNoUsed = 0;
        }

        /// <summary>
        /// What the simulation should use for <paramref name="inputFrame"/>, and
        /// whether it had to be guessed.
        /// </summary>
        /// <remarks>
        /// The answer is recorded at the same index it was asked for. Recording it
        /// anywhere else - at the simulation frame that consumed it, say - puts the
        /// input for one frame into another frame's slot, and the record is what
        /// every later correction reads to decide whether it is needed. A correction
        /// then corrupts the evidence for the next one.
        /// </remarks>
        public byte Resolve(long inputFrame, out bool predicted)
        {
            byte input;
            if (_arrived.TryGet(inputFrame, out input))
            {
                predicted = false;
            }
            else
            {
                // Not arrived: assume they are still doing what they were doing. A
                // full charge is thirty-six frames of one button, so this is right
                // far more often here than in a game of taps - and when it is
                // wrong, the correction repairs it.
                input = _arrived.Predict(inputFrame);
                predicted = true;
            }

            _used.Record(inputFrame, input);
            return input;
        }

        /// <summary>
        /// The first input frame in the range whose guess disagreed with what
        /// arrived, or -1 if every guess held.
        /// </summary>
        public long FirstWrongInputFrame(long from, long through)
        {
            for (long frame = from; frame <= through; frame++)
            {
                byte actual;
                if (!_arrived.TryGet(frame, out actual))
                {
                    SkippedNoActual++;
                    continue;
                }

                byte used;
                if (!_used.TryGet(frame, out used))
                {
                    // Never simulated with this frame's input, so there is nothing
                    // it could have spoiled.
                    SkippedNoUsed++;
                    continue;
                }

                Compared++;

                if (used != actual)
                {
                    return frame;
                }
            }

            return -1;
        }
    }
}
