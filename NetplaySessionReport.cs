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
        private long _resimulated;
        private bool _battle;
        private bool _running;

        public void Start(bool battle)
        {
            Array.Clear(_lag, 0, _lag.Length);
            _frames = 0;
            _rollbacks = 0;
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
                " rollbacks=" + _rollbacks +
                " resimulated=" + _resimulated +
                " lag_p50=" + Percentile(0.50) +
                " lag_p95=" + Percentile(0.95) +
                " lag_p99=" + Percentile(0.99) +
                " lag_max=" + Percentile(1.0) +

                // What each candidate input delay would have left as a rollback
                // rate, read off the histogram rather than inferred from a ping.
                // delay=2 is what this build ran, so miss_d2 should land near the
                // rollback count above - and if it does not, one of the two is
                // measuring something other than what it says.
                " miss_d0=" + MissRate(0) +
                " miss_d1=" + MissRate(1) +
                " miss_d2=" + MissRate(2) +
                " miss_d3=" + MissRate(3) +
                " miss_d4=" + MissRate(4) +
                " miss_d5=" + MissRate(5) +
                " miss_d6=" + MissRate(6)
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
        /// The share of frames an input delay of <paramref name="delay" /> would
        /// still have had to guess across, as a percentage.
        /// </summary>
        private string MissRate(int delay)
        {
            if (_frames == 0)
            {
                return "0.0";
            }

            long covered = 0;
            for (int i = 0; i <= delay && i < Buckets; i++)
            {
                covered += _lag[i];
            }

            return (100.0 * (_frames - covered) / _frames).ToString("F1");
        }
    }
}
