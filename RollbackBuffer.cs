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
    /// **The size is a trade, not a tuning knob.** Longer tolerates worse
    /// connections and costs memory and, worse, recomputation - a rollback of N
    /// frames replays N frames of every player and every block behaviour inside one
    /// real frame. Sixty is one second at this game's fixed step, which is far
    /// beyond the tens of milliseconds a prediction is normally wrong by, and still
    /// cheap enough to replay: Jump King's physics is four lines and its player
    /// state is a handful of fields.
    ///
    /// A pause is deliberately not covered by this. Ten seconds is six hundred
    /// frames and replaying those through every gimmick mod inside one frame is not
    /// something to attempt, which is why a pause stops everybody in interference
    /// mode rather than being absorbed here.
    /// </summary>
    internal sealed class RollbackBuffer
    {
        /// <summary>One second at sixty frames per second.</summary>
        public const int Frames = 60;

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
