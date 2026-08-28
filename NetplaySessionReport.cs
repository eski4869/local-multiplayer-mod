using System;
using System.Diagnostics;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// What one session cost, counted in memory and written once when it ends.
    ///
    /// Deliberately not the per-second <c>netplay cost:</c> line. That one exists
    /// to watch a fault while it happens, and it is far too much to ask of a
    /// session with another person in it. This is one line, written nowhere until
    /// the session is over, carrying no name and no identifier - only counts.
    ///
    /// The measurement that matters is the distribution of <c>lag</c>: how far
    /// ahead of confirmed remote input the simulation is running. An input delay
    /// of D removes the need to guess exactly when lag is within D, so the share
    /// of frames whose lag exceeded D <em>is</em> the rollback rate that delay
    /// would have left. That is why the histogram is kept rather than an average:
    /// the number an automatic delay needs is a tail, and a tail cannot be
    /// recovered from a mean or guessed from a ping.
    /// </summary>
    internal sealed class NetplaySessionReport
    {
        /// <summary>
        /// Lag beyond the prediction window is already past what any delay could
        /// rescue, so the range only has to cover the useful part. Everything
        /// above lands in the final bucket, which keeps the tail honest without
        /// keeping the whole range.
        /// </summary>
        private const int Buckets = 33;

        private readonly long[] _lag = new long[Buckets];
        private readonly Stopwatch _elapsed = new Stopwatch();

        private long _frames;
        private long _rollbacks;
        private long _stalls;
        private long _resimulated;
        private bool _battle;
        private bool _running;

        public void Start(bool battle)
        {
            Array.Clear(_lag, 0, _lag.Length);
            _frames = 0;
            _rollbacks = 0;
            _stalls = 0;
            _resimulated = 0;
            _battle = battle;
            _running = true;
            _elapsed.Reset();
            _elapsed.Start();
        }

        /// <summary>
        /// One sample per simulated frame. An array increment, so it costs the
        /// same whether or not anyone ever reads it.
        /// </summary>
        public void NoteFrame(long lag)
        {
            if (!_running)
            {
                return;
            }

            _frames++;
            _lag[lag < 0 ? 0 : (lag >= Buckets ? Buckets - 1 : (int)lag)]++;
        }

        /// <summary>
        /// The prediction window filled and the game was held still. The one
        /// failure here a player actually feels, so it is counted separately from
        /// everything that merely costs work.
        /// </summary>
        public void NoteStall()
        {
            if (!_running)
            {
                return;
            }

            _stalls++;
        }

        public void NoteRollback(long replayed)
        {
            if (!_running)
            {
                return;
            }

            _rollbacks++;
            _resimulated += replayed;
        }

        /// <summary>
        /// Returns the line to log and disarms, so a session that ends twice - a
        /// peer going silent into a leave, say - cannot report itself twice.
        /// Returns null when there is nothing worth a line.
        ///
        /// Returning the text rather than writing it keeps every number here
        /// reachable from a test without a file anywhere near it. The arithmetic
        /// is the part that can be wrong quietly.
        /// </summary>
        public string Finish()
        {
            if (!_running)
            {
                return null;
            }

            _running = false;
            _elapsed.Stop();

            if (_frames == 0)
            {
                return null;
            }

            return (
                "session battle=" + (_battle ? 1 : 0) +
                " seconds=" +
                    (_elapsed.ElapsedMilliseconds / 1000.0).ToString("F1") +
                " frames=" + _frames +
                " fps=" + (_elapsed.ElapsedMilliseconds <= 0
                    ? "0.0"
                    : (_frames * 1000.0 /
                        _elapsed.ElapsedMilliseconds).ToString("F1")) +
                " delay=" + RollbackPlan.InputDelayFrames +

                // Guessed is how often the peer's input had not arrived yet.
                // Wrong is how often that guess turned out to be wrong, as a share
                // of the guesses - and it is the only one of the two that costs
                // anything, because a correct guess is simply the answer.
                //
                // These were one number called miss_dN, described as the rollback
                // rate a delay would leave. It is not: a first real session had
                // fifty per cent of frames guessed and twelve rollbacks in nine
                // thousand. Jump King holds an input through a whole charge and a
                // whole fall, so "the same as last frame" is nearly always right.
                " guessed=" + GuessRate(RollbackPlan.InputDelayFrames) +
                " wrong=" + WrongShareOfGuesses() +
                " rollbacks=" + _rollbacks +
                " resimulated=" + _resimulated +

                // The window filling is the one failure a player feels, so it is
                // reported on its own rather than folded in with the rest.
                " stalls=" + _stalls +
                " lag_p50=" + Percentile(0.50) +
                " lag_p95=" + Percentile(0.95) +
                " lag_p99=" + Percentile(0.99) +
                " lag_max=" + Percentile(1.0) +

                // What share of frames each candidate delay would still have had
                // to guess across. Read off the histogram rather than inferred
                // from a ping - and worth reading next to wrong= above, because
                // guessing more is only worth avoiding if the guesses are bad.
                " guess_d0=" + GuessRate(0) +
                " guess_d1=" + GuessRate(1) +
                " guess_d2=" + GuessRate(2) +
                " guess_d3=" + GuessRate(3) +
                " guess_d4=" + GuessRate(4) +
                " guess_d5=" + GuessRate(5) +
                " guess_d6=" + GuessRate(6)
            );
        }

        /// <summary>
        /// The smallest lag at or below which the given share of frames fell.
        /// Reported as a frame count, since that is the unit a delay is set in.
        /// </summary>
        private long Percentile(double share)
        {
            long target = (long)Math.Ceiling(_frames * share);
            long seen = 0;

            for (int i = 0; i < Buckets; i++)
            {
                seen += _lag[i];
                if (seen >= target)
                {
                    return i;
                }
            }

            return Buckets - 1;
        }

        /// <summary>
        /// How many of the guesses turned out wrong, as a percentage of the
        /// guesses rather than of every frame. Nothing is spent on a guess that
        /// was right.
        /// </summary>
        private string WrongShareOfGuesses()
        {
            long guessed = GuessedFrames(RollbackPlan.InputDelayFrames);
            if (guessed <= 0)
            {
                return "0.00";
            }

            return (100.0 * _rollbacks / guessed).ToString("F2");
        }

        private long GuessedFrames(int delay)
        {
            long covered = 0;
            for (int i = 0; i <= delay && i < Buckets; i++)
            {
                covered += _lag[i];
            }

            return _frames - covered;
        }

        /// <summary>
        /// The share of frames an input delay of <paramref name="delay" /> would
        /// still have had to guess across, as a percentage.
        /// </summary>
        private string GuessRate(int delay)
        {
            if (_frames == 0)
            {
                return "0.0";
            }

            return (100.0 * GuessedFrames(delay) / _frames).ToString("F1");
        }
    }
}
