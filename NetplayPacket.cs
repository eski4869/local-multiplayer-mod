using System;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// The bytes that cross the wire.
    ///
    /// Binary rather than text, and small enough that the whole input packet fits
    /// in twenty-one bytes: <c>4 + 1 + 16</c>. The existing multiplayer mod sends
    /// ASCII-encoded JSON at about 140 bytes per update, of which 85 are field
    /// names. That is not a style preference - a packet that fits comfortably below
    /// any MTU never fragments, and fragmentation is what turns one lost datagram
    /// into a lost burst.
    ///
    /// Every field is written little-endian explicitly rather than through
    /// <see cref="BitConverter"/>, so a machine of the other endianness would still
    /// agree. Steam only ships little-endian platforms today; writing it out costs
    /// nothing and removes a silent assumption.
    /// </summary>
    internal static class NetplayPacket
    {
        /// <summary>
        /// Bumped whenever the wire format changes in a way an older build would
        /// misread. Checked at handshake so a mismatch is refused with a reason
        /// rather than showing up as a peer who behaves strangely.
        /// </summary>
        public const byte ProtocolVersion = 5;

        public enum Kind : byte
        {
            /// <summary>Who I am and what I am playing. Sent until answered.</summary>
            Hello = 1,

            /// <summary>One player's inputs, ending at a frame.</summary>
            Input = 2,

            /// <summary>I have paused; stop advancing.</summary>
            Pause = 3,

            /// <summary>I have resumed.</summary>
            Resume = 4,

            /// <summary>
            /// The host declaring where the session begins.
            ///
            /// Two machines cannot share a simulation they did not start from the
            /// same state, and rollback cannot rescue that: it replays from a
            /// snapshot, so a snapshot that was already wrong stays wrong however
            /// often it is replayed. Each side placing the players wherever it
            /// happened to be looks like drift with no correction, and is really
            /// two different worlds that never agreed.
            /// </summary>
            Start = 5,

            /// <summary>
            /// A digest of the simulation at a frame, for spotting divergence.
            ///
            /// Determinism is a claim, and an unchecked one fails quietly - by the
            /// time two positions differ visibly they have been differing for a
            /// while. Comparing a number every so often turns that into something
            /// with a frame attached to it.
            /// </summary>
            Checksum = 6,

            /// <summary>
            /// Where both players are, sent by the host to put a session back
            /// together when recomputing can no longer do it.
            /// </summary>
            /// <remarks>
            /// **This deliberately breaks the determinism the rest of the design
            /// rests on**, and that is the right trade at the point it is used.
            ///
            /// A correction returns to a saved frame and replays. Past the buffer
            /// there is no saved frame to return to, so there is nothing to replay
            /// from and no amount of further correction helps - the two machines
            /// simply carry on in worlds that no longer match. What that was doing
            /// until now was reporting "desynchronised" and drifting, which
            /// preserves the principle by abandoning the purpose.
            ///
            /// Determinism is not the goal. It is the cheap way to keep two
            /// machines agreeing, and where it cannot, agreeing still matters more.
            /// Adopting the host's positions restores that at a known cost: the
            /// guest's own king moves to where the host believed it was, which may
            /// be somewhere it did not go. A player can be struck by something they
            /// had dodged. That is a worse frame than they deserved and a better
            /// session than the alternative, which is two people playing games that
            /// stopped being the same one.
            /// </remarks>
            Sync = 7,

            /// <summary>
            /// I am not going to play with you, and this is why.
            /// </summary>
            /// <remarks>
            /// A refusal used to be enacted without being communicated: the
            /// refusing side reported the reason to its own player and left, and
            /// the other side saw somebody arrive and depart without explanation.
            /// The two people are usually in the same room or the same call, so the
            /// missing half was supplied out loud - "it says our versions are
            /// different" - which works and is not a design.
            ///
            /// The reason travels as a code rather than a sentence. A build that
            /// refuses is by definition a build that might not share this one's
            /// wording, and a numbered reason survives that where text does not.
            /// </remarks>
            Refused = 8
        }

        /// <summary>Why a session was refused.</summary>
        public enum RefusalReason : byte
        {
            Unknown = 0,

            /// <summary>Different builds of this mod.</summary>
            Protocol = 1,

            /// <summary>Different levels, or different versions of one.</summary>
            Level = 2
        }

        /// <summary>Largest packet this protocol produces.</summary>
        public const int MaxSize = 128;

        private const int HelloFixedSize = 1 + 1 + 8 + 1 + 1;

        /// <summary>
        /// Writes the handshake: protocol, level identity, and the session shape
        /// both sides have to agree on.
        /// </summary>
        /// <param name="levelHash">
        /// Identifies the level's content, not its name. Two players on "the same"
        /// workshop map can hold different versions of it - workshop items update
        /// in place under one id - and the block layout is what the simulation
        /// depends on.
        /// </param>
        public static int WriteHello(
            byte[] buffer,
            ulong levelHash,
            byte playerCount,
            bool interference
        )
        {
            if (buffer == null || buffer.Length < HelloFixedSize)
            {
                return 0;
            }

            buffer[0] = (byte)Kind.Hello;
            buffer[1] = ProtocolVersion;
            WriteUInt64(buffer, 2, levelHash);
            buffer[10] = playerCount;
            buffer[11] = (byte)(interference ? 1 : 0);
            return HelloFixedSize;
        }

        public static bool ReadHello(
            byte[] buffer,
            int length,
            out byte protocol,
            out ulong levelHash,
            out byte playerCount,
            out bool interference
        )
        {
            protocol = 0;
            levelHash = 0;
            playerCount = 0;
            interference = false;

            if (buffer == null || length < HelloFixedSize ||
                buffer[0] != (byte)Kind.Hello)
            {
                return false;
            }

            protocol = buffer[1];
            levelHash = ReadUInt64(buffer, 2);
            playerCount = buffer[10];
            interference = buffer[11] != 0;
            return true;
        }

        /// <summary>
        /// Writes inputs for consecutive frames ending at
        /// <paramref name="lastFrame"/>.
        /// </summary>
        /// <remarks>
        /// The frame number is the packet's whole addressing scheme. Without it a
        /// receiver can only treat the stream as a queue, which is how "every pause
        /// makes the lag permanently worse" happens: a queue filled in real time
        /// and drained one item per frame never gives back what it accumulated.
        /// </remarks>
        /// <summary>
        /// <paramref name="frameAdvantage"/> is how far behind the peer this sender
        /// believes it is, in frames, negative when it is the one ahead.
        ///
        /// It rides along with the inputs because the receiver cannot work it out
        /// alone. A frame number off the wire is where the sender was when it sent,
        /// so the difference from the receiver's own frame is the true gap plus the
        /// travel time, and the two cannot be separated from one side. They can
        /// from both: the same travel time is in each side's measurement, so
        /// subtracting one from the other cancels it and leaves the gap.
        /// </summary>
        public static int WriteInput(
            byte[] buffer,
            long lastFrame,
            int frameAdvantage,
            byte[] inputs,
            int count
        )
        {
            if (buffer == null || inputs == null || count <= 0 ||
                buffer.Length < 7 + count)
            {
                return 0;
            }

            buffer[0] = (byte)Kind.Input;
            WriteUInt32(buffer, 1, (uint)lastFrame);
            buffer[5] = (byte)count;

            // Clamped into one signed byte. A gap beyond this is not a gap being
            // measured any more, it is a connection that has stopped.
            if (frameAdvantage > sbyte.MaxValue)
            {
                frameAdvantage = sbyte.MaxValue;
            }
            else if (frameAdvantage < sbyte.MinValue)
            {
                frameAdvantage = sbyte.MinValue;
            }

            buffer[6] = unchecked((byte)(sbyte)frameAdvantage);
            Array.Copy(inputs, 0, buffer, 7, count);
            return 7 + count;
        }

        public static bool ReadInput(
            byte[] buffer,
            int length,
            out long lastFrame,
            out int frameAdvantage,
            out int count,
            out int offset
        )
        {
            lastFrame = 0;
            frameAdvantage = 0;
            count = 0;
            offset = 7;

            if (buffer == null || length < 7 || buffer[0] != (byte)Kind.Input)
            {
                return false;
            }

            lastFrame = ReadUInt32(buffer, 1);
            count = buffer[5];
            frameAdvantage = unchecked((sbyte)buffer[6]);

            // A truncated or lying length must not be trusted into a read past the
            // end: this buffer came off the network.
            if (count <= 0 || 7 + count > length)
            {
                count = 0;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Where the session begins, as the host sees it.
        /// </summary>
        /// <remarks>
        /// The coordinates cross as raw IEEE-754 bits rather than text, so both
        /// machines hold the identical value. The design's rule against float in
        /// the sync path is about a value that would be re-derived every frame from
        /// something that had been through a decimal round trip; this is agreed
        /// once, exactly, and then never sent again.
        /// </remarks>
        /// <summary>
        /// Writes both players' positions and velocities at a frame.
        /// </summary>
        /// <remarks>
        /// Velocity as well as position, because a king put in the right place with
        /// the wrong momentum leaves immediately and the two machines are apart
        /// again on the next frame. Position is where the repair shows; velocity is
        /// what makes it hold.
        ///
        /// Raw IEEE-754 bits, as the start packet does, so both machines hold the
        /// identical value rather than two decimal roundings of it.
        /// </remarks>
        public static int WriteSync(byte[] buffer, long frame, float[] values, int count)
        {
            if (buffer == null || values == null || count <= 0 ||
                buffer.Length < 6 + count * 4)
            {
                return 0;
            }

            buffer[0] = (byte)Kind.Sync;
            WriteUInt32(buffer, 1, (uint)frame);
            buffer[5] = (byte)count;

            for (int i = 0; i < count; i++)
            {
                WriteUInt32(buffer, 6 + i * 4, ToBits(values[i]));
            }

            return 6 + count * 4;
        }

        public static bool ReadSync(
            byte[] buffer,
            int length,
            out long frame,
            float[] values,
            out int count
        )
        {
            frame = 0;
            count = 0;

            if (buffer == null || values == null || length < 6 ||
                buffer[0] != (byte)Kind.Sync)
            {
                return false;
            }

            frame = ReadUInt32(buffer, 1);
            int claimed = buffer[5];

            // A truncated or lying length must not be trusted into a read past the
            // end: this buffer came off the network.
            if (claimed <= 0 || claimed > values.Length ||
                6 + claimed * 4 > length)
            {
                return false;
            }

            for (int i = 0; i < claimed; i++)
            {
                values[i] = FromBits(ReadUInt32(buffer, 6 + i * 4));
            }

            count = claimed;
            return true;
        }

        /// <summary>Refusing a session, with the reason.</summary>
        public static int WriteRefused(byte[] buffer, RefusalReason reason)
        {
            if (buffer == null || buffer.Length < 2)
            {
                return 0;
            }

            buffer[0] = (byte)Kind.Refused;
            buffer[1] = (byte)reason;
            return 2;
        }

        public static bool ReadRefused(
            byte[] buffer,
            int length,
            out RefusalReason reason
        )
        {
            reason = RefusalReason.Unknown;

            if (buffer == null || length < 2 || buffer[0] != (byte)Kind.Refused)
            {
                return false;
            }

            byte raw = buffer[1];

            // A reason this build does not recognise is still a refusal, and
            // saying so beats saying nothing.
            reason = raw > (byte)RefusalReason.Level
                ? RefusalReason.Unknown
                : (RefusalReason)raw;

            return true;
        }

        /// <summary>
        /// Carries the input delay because the host is the one that decides it
        /// and both machines have to hold the same number. Sent with the origin
        /// rather than announced separately: the frame a session starts on and
        /// the offset between a press and its effect are the same fact about
        /// when, and splitting them is how the two sides end up agreeing on one
        /// and not the other.
        /// </summary>
        public static int WriteStart(
            byte[] buffer,
            long frame,
            float x,
            float y,
            int inputDelayFrames
        )
        {
            if (buffer == null || buffer.Length < 14)
            {
                return 0;
            }

            buffer[0] = (byte)Kind.Start;
            WriteUInt32(buffer, 1, (uint)frame);
            WriteUInt32(buffer, 5, ToBits(x));
            WriteUInt32(buffer, 9, ToBits(y));
            buffer[13] = (byte)inputDelayFrames;
            return 14;
        }

        public static bool ReadStart(
            byte[] buffer,
            int length,
            out long frame,
            out float x,
            out float y,
            out int inputDelayFrames
        )
        {
            frame = 0;
            x = 0f;
            y = 0f;
            inputDelayFrames = RollbackPlan.DefaultInputDelayFrames;
            if (buffer == null || length < 14 || buffer[0] != (byte)Kind.Start)
            {
                return false;
            }

            frame = ReadUInt32(buffer, 1);
            x = FromBits(ReadUInt32(buffer, 5));
            y = FromBits(ReadUInt32(buffer, 9));
            inputDelayFrames = buffer[13];
            return true;
        }

        public static int WriteChecksum(byte[] buffer, long frame, uint digest)
        {
            if (buffer == null || buffer.Length < 9)
            {
                return 0;
            }

            buffer[0] = (byte)Kind.Checksum;
            WriteUInt32(buffer, 1, (uint)frame);
            WriteUInt32(buffer, 5, digest);
            return 9;
        }

        public static bool ReadChecksum(
            byte[] buffer,
            int length,
            out long frame,
            out uint digest
        )
        {
            frame = 0;
            digest = 0;
            if (buffer == null || length < 9 || buffer[0] != (byte)Kind.Checksum)
            {
                return false;
            }

            frame = ReadUInt32(buffer, 1);
            digest = ReadUInt32(buffer, 5);
            return true;
        }

        /// <summary>
        /// The float's exact bits, so both machines hold the identical value.
        ///
        /// <see cref="BitConverter"/> rather than a pointer cast: it needs no
        /// unsafe context, which would otherwise have to be enabled for every
        /// project that compiles this file, including the test one.
        /// </summary>
        private static uint ToBits(float value)
        {
            return (uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static float FromBits(uint bits)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        public static int WriteControl(byte[] buffer, Kind kind)
        {
            if (buffer == null || buffer.Length < 1 ||
                (kind != Kind.Pause && kind != Kind.Resume))
            {
                return 0;
            }

            buffer[0] = (byte)kind;
            return 1;
        }

        public static bool TryReadKind(byte[] buffer, int length, out Kind kind)
        {
            kind = default(Kind);
            if (buffer == null || length < 1)
            {
                return false;
            }

            byte raw = buffer[0];
            if (raw < (byte)Kind.Hello || raw > (byte)Kind.Refused)
            {
                return false;
            }

            kind = (Kind)raw;
            return true;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return buffer[offset] |
                ((uint)buffer[offset + 1] << 8) |
                ((uint)buffer[offset + 2] << 16) |
                ((uint)buffer[offset + 3] << 24);
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                buffer[offset + i] = (byte)(value >> (i * 8));
            }
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)buffer[offset + i] << (i * 8);
            }

            return value;
        }
    }
}
