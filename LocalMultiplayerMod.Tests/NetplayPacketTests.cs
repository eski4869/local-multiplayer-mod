using LocalMultiplayerMod;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Covers the wire format, including what it does with bytes it should not
    /// trust.
    ///
    /// These buffers arrive from another machine over an unreliable channel, so a
    /// truncated packet is an ordinary event rather than an attack. A length field
    /// read without checking it against what actually arrived is the classic way
    /// that turns into a read past the end of the buffer.
    /// </summary>
    [TestClass]
    public class NetplayPacketTests
    {
        private const byte Left = 1;
        private const byte Jump = 4;

        [TestMethod]
        public void HelloSurvivesARoundTrip()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            int length = NetplayPacket.WriteHello(buffer, 0xDEADBEEFCAFEF00D, 2, true);

            byte protocol;
            ulong levelHash;
            byte playerCount;
            bool interference;
            bool ok = NetplayPacket.ReadHello(
                buffer,
                length,
                out protocol,
                out levelHash,
                out playerCount,
                out interference
            );

            Assert.IsTrue(ok);
            Assert.AreEqual(NetplayPacket.ProtocolVersion, protocol);
            Assert.AreEqual(0xDEADBEEFCAFEF00D, levelHash);
            Assert.AreEqual((byte)2, playerCount);
            Assert.IsTrue(interference);
        }

        [TestMethod]
        public void InputSurvivesARoundTrip()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var inputs = new byte[] { Left, Left, Jump };
            int length = NetplayPacket.WriteInput(buffer, 1234, -3, inputs, inputs.Length);

            long lastFrame;
            int frameAdvantage;
            int count;
            int offset;
            bool ok = NetplayPacket.ReadInput(
                buffer,
                length,
                out lastFrame,
                out frameAdvantage,
                out count,
                out offset
            );

            Assert.IsTrue(ok);
            Assert.AreEqual(1234, lastFrame);
            Assert.AreEqual(3, count);
            Assert.AreEqual(Left, buffer[offset]);
            Assert.AreEqual(Jump, buffer[offset + 2]);

            // Negative means the sender is the one ahead, and it has to survive as
            // a negative number: the sign is the entire content of the field. Read
            // back unsigned it would say the sender is 253 frames behind, and the
            // receiver would conclude it was the one that must wait - the exact
            // opposite of what the sender measured.
            Assert.AreEqual(-3, frameAdvantage);
        }

        [TestMethod]
        public void FrameAdvantageClampsRatherThanWrapping()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var inputs = new byte[] { Left };

            int length = NetplayPacket.WriteInput(buffer, 5, 4000, inputs, 1);

            long lastFrame;
            int frameAdvantage;
            int count;
            int offset;
            NetplayPacket.ReadInput(
                buffer,
                length,
                out lastFrame,
                out frameAdvantage,
                out count,
                out offset
            );

            // Far past what one byte holds. Truncating 4000 would give -96 - a
            // large gap reported as its own opposite, which is worse than reporting
            // it as merely large.
            Assert.AreEqual(127, frameAdvantage);
        }

        [TestMethod]
        public void AFullInputPacketIsTwentyThreeBytes()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var inputs = new byte[InputTimeline.PacketFrames];

            int length = NetplayPacket.WriteInput(buffer, 60, 0, inputs, inputs.Length);

            // One byte of kind, four of frame, one of count, one of frame
            // advantage, sixteen of input. The design's figure of 21 predates the
            // kind byte, which pays for carrying the handshake and the pause
            // signals on the same channel rather than inventing a second one, and
            // the advantage byte, which is what lets the two sides tell a real gap
            // from the travel time. At 60Hz this is still about 1.4 KB/s, and small
            // enough never to fragment - which is what keeps one lost datagram from
            // becoming a lost burst.
            Assert.AreEqual(23, length);
        }

        [TestMethod]
        public void RejectsAnInputPacketClaimingMoreThanArrived()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var inputs = new byte[InputTimeline.PacketFrames];
            NetplayPacket.WriteInput(buffer, 60, 0, inputs, inputs.Length);

            long lastFrame;
            int frameAdvantage;
            int count;
            int offset;

            // The header says sixteen frames follow; only four bytes arrived.
            bool ok = NetplayPacket.ReadInput(
                buffer,
                8,
                out lastFrame,
                out frameAdvantage,
                out count,
                out offset
            );

            Assert.IsFalse(ok);
            Assert.AreEqual(0, count);
        }

        [TestMethod]
        public void RejectsATruncatedHello()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            NetplayPacket.WriteHello(buffer, 1, 2, false);

            byte protocol;
            ulong levelHash;
            byte playerCount;
            bool interference;

            Assert.IsFalse(NetplayPacket.ReadHello(
                buffer,
                4,
                out protocol,
                out levelHash,
                out playerCount,
                out interference
            ));
        }

        [TestMethod]
        public void RejectsAPacketOfTheWrongKind()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            int length = NetplayPacket.WriteControl(
                buffer,
                NetplayPacket.Kind.Pause
            );

            long lastFrame;
            int frameAdvantage;
            int count;
            int offset;

            Assert.IsFalse(NetplayPacket.ReadInput(
                buffer,
                length,
                out lastFrame,
                out frameAdvantage,
                out count,
                out offset
            ));
        }

        [TestMethod]
        public void RejectsAnUnknownKind()
        {
            var buffer = new byte[] { 99 };

            NetplayPacket.Kind kind;
            Assert.IsFalse(NetplayPacket.TryReadKind(buffer, 1, out kind));
        }

        [TestMethod]
        public void StartCarriesCoordinatesExactly()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            const float X = 214.52911f;
            const float Y = -39914.375f;

            int length = NetplayPacket.WriteStart(buffer, 4242, X, Y, 3);

            float x;
            float y;
            long frame;
            int delay;
            Assert.IsTrue(
                NetplayPacket.ReadStart(
                    buffer,
                    length,
                    out frame,
                    out x,
                    out y,
                    out delay
                )
            );

            // The delay rides with the origin because both machines must act on a
            // press at the same frame, and only the host decides which frame that
            // is. Splitting the two apart is how they end up agreeing on when a
            // session began and not on when an input lands.
            Assert.AreEqual(3, delay);

            // The frame matters as much as the coordinates: without it the two
            // machines agreed where the players stood and not when, and the same
            // frame number meant a different amount of elapsed simulation on each.
            Assert.AreEqual(4242, frame);

            // Bit-exact, not merely close. The two machines are agreeing where a
            // shared simulation begins, and a value that differed in its last bit
            // would put them in different worlds from the first frame.
            Assert.AreEqual(X, x, 0f);
            Assert.AreEqual(Y, y, 0f);
        }

        [TestMethod]
        public void StartSurvivesValuesThatAreNotRoundNumbers()
        {
            var buffer = new byte[NetplayPacket.MaxSize];

            foreach (float value in new[]
            {
                0f, -0f, 0.1f, -1234.5678f, float.Epsilon, 1e20f
            })
            {
                int length = NetplayPacket.WriteStart(buffer, 1, value, value, 0);

                float x;
                float y;
                long frame;
                int delay;
                NetplayPacket.ReadStart(
                    buffer,
                    length,
                    out frame,
                    out x,
                    out y,
                    out delay
                );
                Assert.AreEqual(value, x, 0f);
            }
        }

        [TestMethod]
        public void ChecksumSurvivesARoundTrip()
        {
            var buffer = new byte[NetplayPacket.MaxSize];

            int length = NetplayPacket.WriteChecksum(buffer, 3600, 0xFEEDFACE);

            long frame;
            uint digest;
            Assert.IsTrue(
                NetplayPacket.ReadChecksum(buffer, length, out frame, out digest)
            );
            Assert.AreEqual(3600, frame);
            Assert.AreEqual(0xFEEDFACE, digest);
        }

        [TestMethod]
        public void RejectsATruncatedStartOrChecksum()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            NetplayPacket.WriteStart(buffer, 7, 1f, 2f, 2);

            float x;
            float y;
            long startFrame;
            int delay;
            Assert.IsFalse(
                NetplayPacket.ReadStart(
                    buffer,
                    5,
                    out startFrame,
                    out x,
                    out y,
                    out delay
                )
            );

            NetplayPacket.WriteChecksum(buffer, 1, 2);
            long frame;
            uint digest;
            Assert.IsFalse(
                NetplayPacket.ReadChecksum(buffer, 5, out frame, out digest)
            );
        }

        [TestMethod]
        public void RejectsAnEmptyPacket()
        {
            NetplayPacket.Kind kind;
            Assert.IsFalse(NetplayPacket.TryReadKind(new byte[0], 0, out kind));
        }

        [TestMethod]
        public void ControlPacketsRoundTrip()
        {
            var buffer = new byte[NetplayPacket.MaxSize];

            int length = NetplayPacket.WriteControl(
                buffer,
                NetplayPacket.Kind.Resume
            );

            NetplayPacket.Kind kind;
            Assert.IsTrue(NetplayPacket.TryReadKind(buffer, length, out kind));
            Assert.AreEqual(NetplayPacket.Kind.Resume, kind);
        }

        [TestMethod]
        public void RefusesToWriteIntoABufferTooSmall()
        {
            var tiny = new byte[4];

            Assert.AreEqual(0, NetplayPacket.WriteHello(tiny, 1, 2, false));
            Assert.AreEqual(
                0,
                NetplayPacket.WriteInput(tiny, 1, 0, new byte[16], 16)
            );
        }

        [TestMethod]
        public void FrameNumbersSurviveBeyondAShortSession()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var inputs = new byte[] { Jump };

            // Roughly nine hours at 60fps, well past any session, and still exact.
            int length = NetplayPacket.WriteInput(buffer, 2000000, 0, inputs, 1);

            long lastFrame;
            int frameAdvantage;
            int count;
            int offset;
            NetplayPacket.ReadInput(
                buffer,
                length,
                out lastFrame,
                out frameAdvantage,
                out count,
                out offset
            );

            Assert.AreEqual(2000000, lastFrame);
        }
    }
}
