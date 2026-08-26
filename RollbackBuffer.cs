using System.Collections.Generic;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// The frames a rollback can rewind to.
    ///
    /// One snapshot per player per frame, kept for a bounded window. The window is
    /// the limit on how wrong a prediction may be before it cannot be repaired:
    /// past it, the confirmed frame has already been discarded and there is nothing
    /// to return to.
    ///
    /// **The size is not a tuning knob.** It follows from the prediction window:
    /// nothing may be guessed further ahead than that, so no disagreement can be
    /// older than it, so nothing will ever ask for a frame beyond it. Making it
    /// larger does not tolerate worse connections - it only retains snapshots for
    /// corrections that cannot happen. See <see cref="RollbackPlan"/>, which holds
    /// the numbers and the relationships between them.
    ///
    /// Frames are only stored while something is being guessed at, so this window
    /// is a bound on how far back it reaches rather than a promise that every frame
    /// inside it is present.
    ///
    /// A pause is deliberately not covered by this. Ten seconds is six hundred
    /// frames and replaying those through every gimmick mod inside one frame is not
    /// something to attempt, which is why a pause stops everybody in interference
    /// mode rather than being absorbed here.
    /// </summary>
    internal sealed class RollbackBuffer
    {
        /// <summary>
        /// Comfortably past the prediction window, and no further.
        ///
        /// A second's worth was kept here on the reasoning that longer tolerates
        /// worse connections. It does not: the prediction window caps how far a
        /// guess can be wrong at eight frames, so nothing ever asks for a frame
        /// older than that, and the rest was memory held and snapshots retained for
        /// corrections that cannot happen.
        ///
        /// Sized so the cost ceiling is what refuses a correction, never the
        /// buffer: a correction is capped at MaxRollbackFrames replayed frames and
        /// restores the frame before the first spoiled one, so it can reach back
        /// that many frames plus one. Anything less here would turn corrections
        /// the ceiling allows into "the correction arrived too late", which is a
        /// permanent divergence rather than a skipped repair.
        /// </summary>
        public const int Frames = RollbackPlan.RequiredBufferFrames;

        private readonly Dictionary<int, PlayerSnapshot[]> _byPlayer =
            new Dictionary<int, PlayerSnapshot[]>();

        private long _newestFrame = -1;

        public long NewestFrame
        {
            get { return _newestFrame; }
        }

        /// <summary>The oldest frame still available to rewind to.</summary>
        public long OldestFrame
        {
            get
            {
                long oldest = _newestFrame - Frames + 1;
                return oldest < 0 ? 0 : oldest;
            }
        }

        public void Clear()
        {
            _byPlayer.Clear();
            _newestFrame = -1;
        }

        public void Store(int playerNumber, long frame, PlayerSnapshot snapshot)
        {
            if (snapshot == null || frame < 0)
            {
                return;
            }

            PlayerSnapshot[] slots;
            if (!_byPlayer.TryGetValue(playerNumber, out slots))
            {
                slots = new PlayerSnapshot[Frames];
                _byPlayer[playerNumber] = slots;
            }

            slots[Index(frame)] = snapshot;
            if (frame > _newestFrame)
            {
                _newestFrame = frame;
            }
        }

        public bool TryGet(int playerNumber, long frame, out PlayerSnapshot snapshot)
        {
            snapshot = null;
            if (!CanRewindTo(frame))
            {
                return false;
            }

            PlayerSnapshot[] slots;
            if (!_byPlayer.TryGetValue(playerNumber, out slots))
            {
                return false;
            }

            PlayerSnapshot stored = slots[Index(frame)];

            // The ring slot could hold a snapshot from an earlier lap, which would
            // restore a player to where they were a second ago and look exactly
            // like a successful rollback.
            if (stored == null || stored.Frame != frame)
            {
                return false;
            }

            snapshot = stored;
            return true;
        }

        public bool CanRewindTo(long frame)
        {
            return frame >= 0 && frame >= OldestFrame && frame <= _newestFrame;
        }

        private static int Index(long frame)
        {
            return (int)(frame % Frames);
        }
    }
}
