using System.Diagnostics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// Where this mod's frame time actually goes.
    ///
    /// The netplay report already timed the snapshots and the corrections, and both
    /// read zero on the machine that was struggling - from which the conclusion was
    /// drawn that the mod was not the problem. That conclusion did not follow.
    /// Those two numbers cover the netplay bookkeeping and nothing else, while the
    /// mod's real per-frame additions - a whole second player simulated and drawn,
    /// a scope entered and left around every player update, twenty-seven patched
    /// members redirecting their reads - were never measured at all.
    ///
    /// A machine that used to finish a frame just inside its budget and now runs
    /// two milliseconds over does not need a large cause. It needs a cause of about
    /// two milliseconds, and every candidate here was unmeasured.
    /// </summary>
    internal static class FrameCost
    {
        private static readonly double TicksToMilliseconds =
            1000.0 / Stopwatch.Frequency;

        /// <summary>
        /// The whole simulation - every entity, every player, the lot.
        /// </summary>
        /// <remarks>
        /// Measured the same way whether one player is running or four, which is
        /// what makes the numbers comparable. The per-player figure beside it says
        /// how much of this the added players account for; on its own it cannot say
        /// whether that share is large, because it never measured the first player.
        /// </remarks>
        public static double SimulationMilliseconds;

        /// <summary>Simulating the players this mod added, beyond the first.</summary>
        public static double AdditionalPlayerMilliseconds;

        /// <summary>Entering and leaving a player's scope, and resyncing gimmicks.</summary>
        public static double ScopeMilliseconds;

        /// <summary>
        /// The whole of Game1.Draw - the world, every player, the interface.
        ///
        /// This measured only what this mod added to the drawing, which reads as
        /// zero with one player and so cannot be compared against anything. The
        /// question worth answering is what a frame costs with one player and with
        /// two, and that needs the same thing measured in both.
        /// </summary>
        public static double DrawMilliseconds;

        /// <summary>Extra simulated frames run to close a gap, drawing skipped.</summary>
        public static double CatchUpMilliseconds;

        public static long Now
        {
            get { return Stopwatch.GetTimestamp(); }
        }

        public static void AddSimulation(long since)
        {
            SimulationMilliseconds += (Now - since) * TicksToMilliseconds;
        }

        public static void AddAdditionalPlayer(long since)
        {
            AdditionalPlayerMilliseconds += (Now - since) * TicksToMilliseconds;
        }

        public static void AddScope(long since)
        {
            ScopeMilliseconds += (Now - since) * TicksToMilliseconds;
        }

        public static void AddDraw(long since)
        {
            DrawMilliseconds += (Now - since) * TicksToMilliseconds;
        }

        public static void AddCatchUp(long since)
        {
            CatchUpMilliseconds += (Now - since) * TicksToMilliseconds;
        }

        public static void Reset()
        {
            SimulationMilliseconds = 0;
            AdditionalPlayerMilliseconds = 0;
            ScopeMilliseconds = 0;
            DrawMilliseconds = 0;
            CatchUpMilliseconds = 0;
        }
    }
}
