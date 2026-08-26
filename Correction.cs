namespace LocalMultiplayerMod
{
    /// <summary>
    /// What a machine needs to be able to do for a correction to be carried out.
    /// </summary>
    /// <remarks>
    /// Everything here is either a number or an effect on the world. Nothing about
    /// Jump King, Steam or Harmony appears, which is what lets the same sequencing
    /// run against a toy simulation in a test.
    /// </remarks>
    internal interface ICorrectionWorld
    {
        /// <summary>
        /// The frame the session is on. It has *not* been simulated yet - the game
        /// runs it as soon as the correction returns.
        /// </summary>
        long CurrentFrame { get; }

        /// <summary>The newest remote input frame known to be real.</summary>
        long RemoteConfirmedThrough { get; }

        /// <summary>How far the search has already been carried.</summary>
        long LastAppliedRemote { get; set; }

        /// <summary>
        /// The first input frame in the range whose guess was wrong, or -1.
        /// </summary>
        long FirstWrongInputFrame(long from, long through);

        bool CanRestore(long simulationFrame);

        bool Restore(long simulationFrame);

        /// <summary>Simulate one frame again and record what it produced.</summary>
        void ReplayFrame(long simulationFrame);

        void Report(string message);
    }

    /// <summary>
    /// Carrying out a correction: deciding whether one is needed, whether it is
    /// affordable, and performing it in an order that cannot leave the world
    /// half-repaired.
    /// </summary>
    /// <remarks>
    /// **The order is the substance.** Every failure this code has had was a step in
    /// the wrong place rather than a step computed wrongly: giving up on a
    /// correction after the world had already been rewound, so the players were
    /// dragged backwards and left there; replaying the frame the game was about to
    /// simulate itself, so every correction pushed the world one frame ahead of its
    /// own counter, permanently and undetectably.
    ///
    /// Sequencing errors do not throw and do not show up in a profile. They show up
    /// as somebody saying the game feels wrong, several days later. So the sequence
    /// lives here, apart from the game, where a test can watch every step.
    /// </remarks>
    internal static class Correction
    {
        public enum Outcome
        {
            /// <summary>No new remote input has been confirmed.</summary>
            NothingNewConfirmed,

            /// <summary>Every guess held.</summary>
            NoMisprediction,

            /// <summary>The wrong guess has not been acted on yet.</summary>
            NotYetActedOn,

            /// <summary>The frame to return to is no longer held.</summary>
            TooLate,

            /// <summary>More frames to replay than one real frame can afford.</summary>
            TooExpensive,

            /// <summary>The state could not be put back.</summary>
            RestoreFailed,

            /// <summary>Carried out.</summary>
            Applied
        }

        public struct Result
        {
            public Outcome Outcome;

            /// <summary>The simulation frame whose state was restored.</summary>
            public long RestoreTo;

            /// <summary>The first simulation frame computed from the wrong guess.</summary>
            public long FirstSpoiled;

            /// <summary>How many frames were replayed.</summary>
            public int Replayed;
        }

        public static Result Run(ICorrectionWorld world)
        {
            var result = new Result();
            result.RestoreTo = -1;
            result.FirstSpoiled = -1;

            long current = world.CurrentFrame;

            // Never past the frame actually simulated. Confirmed input runs ahead of
            // the simulation whenever this machine is behind the peer - those
            // packets describe frames it has not reached, which have no guess to be
            // wrong about. Letting the marker follow the confirmed frame past the
            // present put every misprediction at or before the current frame below
            // the range searched, where it was never examined again: the machine
            // that lags becomes the one that stops correcting, and it is the only
            // one that needs to.
            long confirmed = world.RemoteConfirmedThrough;
            if (confirmed > current)
            {
                confirmed = current;
            }

            if (confirmed <= world.LastAppliedRemote)
            {
                result.Outcome = Outcome.NothingNewConfirmed;
                return result;
            }

            long wrong = world.FirstWrongInputFrame(
                world.LastAppliedRemote + 1,
                confirmed
            );

            if (wrong < 0)
            {
                world.LastAppliedRemote = confirmed;
                result.Outcome = Outcome.NoMisprediction;
                return result;
            }

            RollbackPlan.Plan plan = RollbackPlan.For(wrong, current);
            result.FirstSpoiled = plan.FirstSpoiled;
            result.RestoreTo = plan.RestoreTo;

            if (!plan.Needed)
            {
                // The frame that will consume the wrong guess has not run. It will
                // read the real input, which has now arrived.
                world.LastAppliedRemote = confirmed;
                result.Outcome = Outcome.NotYetActedOn;
                return result;
            }

            // Both refusals are decided before anything is touched. A correction
            // abandoned after the restore leaves the world in the past with nothing
            // to carry it forward, which is worse than not correcting at all: give
            // up before touching anything, or not at all.
            if (plan.ReplayFrames > RollbackPlan.MaxRollbackFrames)
            {
                world.Report(
                    "correction skipped - " + plan.ReplayFrames + " frames behind"
                );
                world.LastAppliedRemote = confirmed;
                result.Outcome = Outcome.TooExpensive;
                return result;
            }

            if (!world.CanRestore(plan.RestoreTo))
            {
                world.Report("desynchronised - the correction arrived too late");
                world.LastAppliedRemote = confirmed;
                result.Outcome = Outcome.TooLate;
                return result;
            }

            if (!world.Restore(plan.RestoreTo))
            {
                world.Report("desynchronised - could not rewind");
                world.LastAppliedRemote = confirmed;
                result.Outcome = Outcome.RestoreFailed;
                return result;
            }

            // Up to but not including the current frame, which the game is about to
            // simulate itself.
            for (long frame = plan.FirstSpoiled; frame < current; frame++)
            {
                world.ReplayFrame(frame);
            }

            world.LastAppliedRemote = confirmed;
            result.Replayed = plan.ReplayFrames;
            result.Outcome = Outcome.Applied;
            return result;
        }
    }
}
