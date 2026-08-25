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
        public const byte ProtocolVersion = 1;

        public enum Kind : byte
        {
            /// <summary>Who I am and what I am playing. Sent until answered.</summary>
            Hello = 1,

            /// <summary>One player's inputs, ending at a frame.</summary>
            Input = 2,

            /// <summary>I have paused; stop advancing.</summary>
            Pause = 3,

            /// <summary>I have resumed.</summary>
            Resume = 4
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
        public static int WriteInput(
            byte[] buffer,
            long lastFrame,
            byte[] inputs,
            int count
        )
        {
            if (buffer == null || inputs == null || count <= 0 ||
                buffer.Length < 6 + count)
            {
                return 0;
            }

            buffer[0] = (byte)Kind.Input;
            WriteUInt32(buffer, 1, (uint)lastFrame);
            buffer[5] = (byte)count;
            Array.Copy(inputs, 0, buffer, 6, count);
            return 6 + count;
        }

        public static bool ReadInput(
            byte[] buffer,
            int length,
            out long lastFrame,
            out int count,
            out int offset
        )
        {
            lastFrame = 0;
            count = 0;
            offset = 6;

            if (buffer == null || length < 6 || buffer[0] != (byte)Kind.Input)
            {
                return false;
            }

            lastFrame = ReadUInt32(buffer, 1);
            count = buffer[5];

            // A truncated or lying length must not be trusted into a read past the
            // end: this buffer came off the network.
            if (count <= 0 || 6 + count > length)
            {
                count = 0;
                return false;
            }

            return true;
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
            if (raw < (byte)Kind.Hello || raw > (byte)Kind.Resume)
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
