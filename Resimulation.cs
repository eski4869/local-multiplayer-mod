namespace LocalMultiplayerMod
{
    /// <summary>
    /// Whether the game is recomputing frames it has already played.
    ///
    /// Rollback runs the same frame more than once. The simulation must produce the
    /// same answer every time, which it will; presentation must happen exactly
    /// once, which it will not unless something tells it. Left alone, a rollback
    /// over twenty frames replays every landing sound twenty times, shakes the
    /// screen twenty times, and writes the save file twenty times.
    ///
    /// So the rule is a split rather than a list: **simulation ignores this flag,
    /// presentation reads it.** Anything that only changes what the player sees or
    /// hears asks here first. Anything that decides where a player ends up must
    /// not, or the recomputation would stop matching the original.
    ///
    /// Exposed through <see cref="LocalMultiplayerApi"/> so a third-party mod can
    /// honour it too. A mod that does not is not broken in single player and does
    /// not desynchronise anyone - it just repeats its own effects, which is a
    /// visible fault rather than a silent one.
    ///
    /// This is also useful outside netplay: the same flag is what a replay viewer
    /// or a rewind-practice mode would need.
    /// </summary>
    internal static class Resimulation
    {
        private static int _depth;

        /// <summary>
        /// True while frames are being recomputed. Presentation should suppress
        /// itself; simulation should behave exactly as it did the first time.
        /// </summary>
        public static bool IsActive
        {
            get { return _depth > 0; }
        }

        /// <summary>
        /// Nested rather than a plain bool, so an inner scope cannot end an outer
        /// one early. Rollback is entered once, but the API lets a mod drive its
        /// own recomputation inside ours.
        /// </summary>
        public static Scope Enter()
        {
            _depth++;
            return new Scope(true);
        }

        /// <summary>
        /// Clears a depth left behind when an exception escaped a recomputation.
        /// Called once per frame at a point where the depth is known to be zero, so
        /// one bad frame cannot suppress every sound for the rest of the run.
        /// </summary>
        public static void ResetIfLeaked()
        {
            _depth = 0;
        }

        internal struct Scope : System.IDisposable
        {
            private bool _entered;

            public Scope(bool entered)
            {
                _entered = entered;
            }

            public void Dispose()
            {
                if (!_entered)
                {
                    return;
                }

                _entered = false;
                if (_depth > 0)
                {
                    _depth--;
                }
            }
        }
    }
}
