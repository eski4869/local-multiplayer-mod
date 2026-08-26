namespace LocalMultiplayerMod
{
    /// <summary>
    /// Works out what a correction has to do, in frames.
    ///
    /// This is separated from the session because it is the part that was wrong,
    /// and it is arithmetic. A disagreement is found in one index space and repaired
    /// in another: inputs are recorded at the frame the pad was read, snapshots at
    /// the frame the simulation produced them, and the input delay sits between the
    /// two. Confusing them changes nothing visible - no exception, no log, no spike
    /// in the cost report - the world simply ends up a fixed distance from where it
    /// belongs, every single time, which is the hardest kind of error to see and the
    /// easiest kind to check.
    ///
    /// Here it can be checked on paper, and in tests, instead of by two people
    /// starting a game.
    /// </summary>
    internal static class RollbackPlan
    {
        /// <summary>
        /// How long an input waits before the simulation acts on it.
        ///
        /// Used alongside the rollback rather than instead of it: the delay covers
        /// the common short gap for free, and the rollback covers what the delay
        /// does not.
        /// </summary>
        public const int InputDelayFrames = 2;

        /// <summary>
        /// How far ahead of confirmed input the simulation may guess.
        ///
        /// This is the ceiling on what one correction can cost, because no wrong
        /// guess can be older than this.
        /// </summary>
        public const int MaxPredictionFrames = 8;

        /// <summary>
        /// The most frames one correction may replay inside a single real frame.
        /// </summary>
        public const int MaxRollbackFrames = 20;

        /// <summary>
        /// How many frames of snapshots must be kept.
        ///
        /// The cost ceiling should be the only thing that refuses a correction. A
        /// correction replaying <see cref="MaxRollbackFrames"/> frames restores the
        /// frame before the first spoiled one, so it reaches one further back than
        /// it replays; the spare frame past that keeps the boundary from being
        /// exact.
        /// </summary>
        public const int RequiredBufferFrames = MaxRollbackFrames + 2;

        /// <summary>
        /// The gap worth stopping the game over. A frame or two apart is the normal
        /// condition of two machines and the prediction covers it; stalling for that
        /// trades a gap nobody can feel for a stutter everybody can.
        /// </summary>
        public const int MinWaitFrames = 3;

        /// <summary>
        /// The most frames in a row the game may be held still - about 150ms.
        /// A cap on the response, not on how long a peer may be slow.
        /// </summary>
        public const int MaxStallFrames = 9;

        /// <summary>
        /// How many frames this machine should wait so the peer can catch up, from
        /// the two sides' measurements of each other.
        /// </summary>
        /// <param name="localAdvantage">
        /// How far behind the peer this machine measures itself, negative when it is
        /// the one ahead. Contains the travel time.
        /// </param>
        /// <param name="remoteAdvantage">
        /// The same quantity as the peer measured it, which contains the same travel
        /// time - which is what makes the pair usable when neither is alone.
        /// </param>
        /// <remarks>
        /// Comparing frame numbers directly cannot work, and not because it is
        /// imprecise. The peer's frame number is where it was when it sent, so any
        /// single measurement is the gap and the travel time added together with no
        /// way to tell which is which. Against a threshold that reads as permanent
        /// advantage on any connection slower than the threshold, and the machine
        /// stalls for ever on a link doing nothing wrong.
        ///
        /// Subtracting the two removes the travel time exactly, because it appears
        /// once in each with the same sign. What is left is twice the gap.
        /// </remarks>
        public static int FramesToWait(float localAdvantage, float remoteAdvantage)
        {
            // Level, or behind. The other machine is the one that will wait.
            if (localAdvantage >= remoteAdvantage)
            {
                return 0;
            }

            var frames = (int)(((remoteAdvantage - localAdvantage) / 2f) + 0.5f);
            return frames < MinWaitFrames ? 0 : frames;
        }

        /// <summary>What a correction should do, or that it should not happen.</summary>
        public struct Plan
        {
            /// <summary>False when nothing has been spoiled yet.</summary>
            public bool Needed;

            /// <summary>The first simulation frame computed from the wrong guess.</summary>
            public long FirstSpoiled;

            /// <summary>The simulation frame whose snapshot is restored.</summary>
            public long RestoreTo;

            /// <summary>How many frames are replayed.</summary>
            public int ReplayFrames;
        }

        /// <summary>
        /// Plans the repair of a guess that turned out wrong.
        /// </summary>
        /// <param name="wrongInputFrame">
        /// The index in the input timeline whose guess disagreed with what arrived.
        /// </param>
        /// <param name="currentFrame">
        /// The frame the session is on, which has *not* been simulated yet - the
        /// game runs it as soon as the correction returns.
        /// </param>
        public static Plan For(long wrongInputFrame, long currentFrame)
        {
            var plan = new Plan();

            // The input was not read by the simulation on its own frame. It waited
            // out the input delay, so the frames between are innocent.
            plan.FirstSpoiled = wrongInputFrame + InputDelayFrames;

            // Restoring the frame before it: the state that frame left behind is
            // exactly the state the spoiled frame started from.
            plan.RestoreTo = plan.FirstSpoiled - 1;

            // Nothing has acted on the guess yet. The frame that will consume it
            // has not run, and it will read the real input when it does.
            if (plan.FirstSpoiled >= currentFrame)
            {
                plan.Needed = false;
                plan.ReplayFrames = 0;
                return plan;
            }

            // Up to but not including currentFrame. Replaying that one here as well
            // leaves the world one frame ahead of its own counter - an error every
            // later correction adds to and no later frame can detect.
            plan.ReplayFrames = (int)(currentFrame - plan.FirstSpoiled);
            plan.Needed = true;
            return plan;
        }
    }
}
