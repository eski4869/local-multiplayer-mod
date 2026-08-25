using System;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// One player's inputs, indexed by frame.
    ///
    /// Inputs rather than positions, which is a decision about determinism before
    /// it is one about bandwidth. A position is a float that would have to survive a
    /// round trip through the wire and back into the simulation, and the netplay
    /// design forbids float in the sync path for exactly that reason. An input is
    /// three bits and cannot drift.
    ///
    /// Positions also cannot be rolled back to. Rewinding to frame N and recomputing
    /// needs what was *pressed* at frame N; what the player happened to be standing
    /// on at frame N is an output, not an input.
    ///
    /// **Every packet carries the last <see cref="PacketFrames"/> frames, not just
    /// the newest one.** A dropped packet then costs nothing, because the next one
    /// contains what it was carrying. Only sixteen consecutive losses leave a hole.
    /// That is what buys the right to send unreliable and unordered: no
    /// retransmission, no acknowledgements, and none of the up-to-200ms buffering
    /// that Steam's reliable send would add - and rollback wants freshness over
    /// completeness.
    /// </summary>
    internal sealed class InputTimeline
    {
        /// <summary>
        /// Frames per packet. Sixteen at 60fps is about a quarter second of loss
        /// tolerance, and the whole packet is still
        /// <c>4 + 1 + 16 = 21</c> bytes.
        /// </summary>
        public const int PacketFrames = 16;

        /// <summary>
        /// How far back the timeline remembers. Bounds memory and is the limit on
        /// how far a rollback could ever reach.
        /// </summary>
        private const int Capacity = 256;

        private readonly byte[] _inputs = new byte[Capacity];
        private readonly bool[] _known = new bool[Capacity];

        private long _highestKnown = -1;
        private long _confirmedThrough = -1;

        /// <summary>
        /// The newest frame any input has arrived for. May be ahead of
        /// <see cref="ConfirmedThrough"/> when a packet was lost.
        /// </summary>
        public long HighestKnown
        {
            get { return _highestKnown; }
        }

        /// <summary>
        /// The last frame before the first hole. Everything up to here can be
        /// simulated without predicting, so this is the frame a rollback would
        /// rewind to.
        /// </summary>
        public long ConfirmedThrough
        {
            get { return _confirmedThrough; }
        }

        public void Reset()
        {
            Array.Clear(_known, 0, _known.Length);
            Array.Clear(_inputs, 0, _inputs.Length);
            _highestKnown = -1;
            _confirmedThrough = -1;
        }

        /// <summary>
        /// Stores one frame's input, from local capture or from a packet.
        /// </summary>
        /// <returns>
        /// False when the frame is outside what the timeline still holds - either
        /// already overwritten, or so far ahead that accepting it would discard
        /// frames still needed.
        /// </returns>
        public bool Record(long frame, byte input)
        {
            if (frame < 0 || frame <= _highestKnown - Capacity)
            {
                return false;
            }

            if (frame > _highestKnown)
            {
                // Everything between the old high-water mark and the new frame is
                // not known yet, and must not read as whatever a previous lap
                // through the ring left there.
                for (long f = _highestKnown + 1; f < frame; f++)
                {
                    _known[Index(f)] = false;
                }

                _highestKnown = frame;
            }

            int index = Index(frame);
            _inputs[index] = input;
            _known[index] = true;

            AdvanceConfirmed();
            return true;
        }

        /// <summary>
        /// Applies a packet: <paramref name="inputs"/> are consecutive frames
        /// ending at <paramref name="lastFrame"/>.
        ///
        /// Re-applying frames already held is normal and costs nothing - that
        /// overlap is what makes a lost packet harmless.
        /// </summary>
        public void Receive(long lastFrame, byte[] inputs, int count)
        {
            if (inputs == null || count <= 0)
            {
                return;
            }

            if (count > inputs.Length)
            {
                count = inputs.Length;
            }

            long firstFrame = lastFrame - count + 1;
            for (int i = 0; i < count; i++)
            {
                Record(firstFrame + i, inputs[i]);
            }
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> with the newest
        /// <see cref="PacketFrames"/> frames, oldest first.
        /// </summary>
        /// <returns>How many frames were written.</returns>
        public int BuildPacket(byte[] buffer)
        {
            if (buffer == null || _highestKnown < 0)
            {
                return 0;
            }

            int count = (int)Math.Min(PacketFrames, _highestKnown + 1);
            if (count > buffer.Length)
            {
                count = buffer.Length;
            }

            long firstFrame = _highestKnown - count + 1;
            for (int i = 0; i < count; i++)
            {
                long frame = firstFrame + i;
                buffer[i] = _known[Index(frame)] ? _inputs[Index(frame)] : (byte)0;
            }

            return count;
        }

        /// <summary>Reads one frame's input.</summary>
        /// <returns>False when that frame is a hole or out of range.</returns>
        public bool TryGet(long frame, out byte input)
        {
            input = 0;
            if (frame < 0 || frame > _highestKnown ||
                frame <= _highestKnown - Capacity)
            {
                return false;
            }

            int index = Index(frame);
            if (!_known[index])
            {
                return false;
            }

            input = _inputs[index];
            return true;
        }

        /// <summary>
        /// The input to assume for a frame that has not arrived: whatever was last
        /// held.
        ///
        /// The simplest prediction there is, and it fits this game unusually well.
        /// Input here is made of long holds - a full charge is thirty-six frames of
        /// the same button - so "the same as last frame" is right far more often
        /// than it would be in a game of taps.
        /// </summary>
        public byte Predict(long frame)
        {
            for (long f = Math.Min(frame, _highestKnown); f >= 0 &&
                f > _highestKnown - Capacity; f--)
            {
                byte input;
                if (TryGet(f, out input))
                {
                    return input;
                }
            }

            return 0;
        }

        private void AdvanceConfirmed()
        {
            while (_confirmedThrough < _highestKnown)
            {
                long next = _confirmedThrough + 1;
                if (next <= _highestKnown - Capacity || !_known[Index(next)])
                {
                    return;
                }

                _confirmedThrough = next;
            }
        }

        private static int Index(long frame)
        {
            return (int)(frame % Capacity);
        }
    }
}
