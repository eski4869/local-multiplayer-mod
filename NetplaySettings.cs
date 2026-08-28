using System;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// What the host chooses before a lobby exists, and how Auto turns a
    /// measurement into a number.
    ///
    /// Only the host reads these. A guest is told the answer with the origin
    /// frame and applies it, because the one thing that cannot vary between the
    /// two machines is which frame a press lands on.
    /// </summary>
    internal static class NetplaySettings
    {
        /// <summary>
        /// The frame budget this machine really runs at. Not 16.67: the fixed
        /// step sleeps in whole milliseconds, so a second holds about 58.8 frames
        /// and every millisecond-to-frames conversion here has to use the number
        /// that was measured rather than the one that was asked for.
        /// </summary>
        private const double FrameMilliseconds = 17.0;

        /// <summary>
        /// True while the delay should be worked out from the connection instead
        /// of taken from <see cref="ManualDelayFrames" />.
        /// </summary>
        public static bool AutomaticDelay = true;

        public static int ManualDelayFrames = RollbackPlan.DefaultInputDelayFrames;

        /// <summary>
        /// Decides the delay for a session about to start.
        /// </summary>
        /// <param name="measuredRoundTripMilliseconds">
        /// The handshake's own round trip, or a value at or below zero when
        /// nothing was measured.
        /// </param>
        /// <summary>
        /// How much one-way travel costs nothing worth paying for.
        ///
        /// Four frames is about seventy milliseconds, which covers a domestic
        /// connection with room to spare. Below it Auto does not raise the delay
        /// at all, because there is nothing there to buy - see the remarks on
        /// <see cref="Resolve" />.
        /// </summary>
        private const int FreeTravelFrames = 4;

        /// <remarks>
        /// Auto cannot be settled when the lobby is created, because at that
        /// moment there is nobody on the other end to measure. It is settled when
        /// somebody joins, which is the first point a round trip exists.
        ///
        /// **The delay is not here to cover the travel time**, which is what this
        /// used to compute and it was the wrong objective. Travel decides how many
        /// frames must be *predicted*, and prediction is very nearly free in this
        /// game: a first real session put it at 99.7% correct over nine thousand
        /// frames - fifty per cent of frames predicted, twelve of them wrong. Jump
        /// King holds an input for long stretches, through a charge and through a
        /// whole fall, so "the same as last frame" is almost always right.
        ///
        /// Covering the travel would therefore buy a fraction of one per cent, and
        /// pay for it with input delay on every single frame of a game where the
        /// length of a press is the whole of the mechanic. On that session the old
        /// rule would have chosen three to seven frames. That is the wrong trade.
        ///
        /// Nor does raising the delay avoid the stall, which is the failure that
        /// is actually felt: <c>NetplaySession</c> stalls on the raw distance to
        /// confirmed input, without subtracting the delay, so a larger delay does
        /// not widen that margin.
        ///
        /// So Auto stays at the value the mod ships with and only climbs for a
        /// link far enough away that mispredictions stop being rare. The floor is
        /// deliberately the shipped default rather than something lower: a
        /// measurement is not a licence to change how the game feels, and going
        /// below it is the owner's call, not this function's.
        /// </remarks>
        public static int Resolve(double measuredRoundTripMilliseconds)
        {
            if (!AutomaticDelay)
            {
                return Clamp(ManualDelayFrames);
            }

            if (measuredRoundTripMilliseconds <= 0.0)
            {
                return RollbackPlan.DefaultInputDelayFrames;
            }

            double oneWayFrames =
                measuredRoundTripMilliseconds / 2.0 / FrameMilliseconds;

            int beyondFree = (int)Math.Ceiling(oneWayFrames) - FreeTravelFrames;
            if (beyondFree < 0)
            {
                beyondFree = 0;
            }

            return Clamp(RollbackPlan.DefaultInputDelayFrames + beyondFree);
        }

        private static int Clamp(int frames)
        {
            if (frames < RollbackPlan.MinInputDelayFrames)
            {
                return RollbackPlan.MinInputDelayFrames;
            }

            if (frames > RollbackPlan.MaxInputDelayFrames)
            {
                return RollbackPlan.MaxInputDelayFrames;
            }

            return frames;
        }
    }
}
