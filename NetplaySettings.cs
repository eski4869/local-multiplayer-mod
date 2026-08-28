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
        /// <remarks>
        /// Auto cannot be settled when the lobby is created, because at that
        /// moment there is nobody on the other end to measure. It is settled when
        /// somebody joins, which is the first point a round trip exists.
        ///
        /// One-way travel is what decides whether a frame has to be guessed: the
        /// simulation needs the peer's input for a frame it is already
        /// <c>delay</c> frames past, so a delay covering the travel removes the
        /// guess and anything short of it does not. Half the round trip is that
        /// travel, and it is rounded up rather than to nearest because a delay one
        /// frame short still leaves the rollback doing the work.
        ///
        /// The floor is one. Zero delay is a legitimate manual choice for someone
        /// who wants every frame as early as possible and will take the
        /// corrections, but it is not something to arrive at automatically.
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

            int frames = (int)Math.Ceiling(oneWayFrames);
            if (frames < 1)
            {
                frames = 1;
            }

            return Clamp(frames);
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
