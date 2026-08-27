using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// The packet that puts a session back together when recomputing cannot.
    ///
    /// It carries floats, which the rest of the protocol deliberately avoids. That
    /// rule is about values re-derived every frame from something that has been
    /// through a decimal round trip; this is a value stated once, exactly, and
    /// obeyed - so it crosses as raw bits and has to come back bit-identical.
    /// A position that arrives a millionth out is a position the two machines
    /// disagree about, which is the thing being repaired.
    /// </summary>
    [TestClass]
    public class NetplaySyncPacketTests
    {
        [TestMethod]
        public void PositionsSurviveExactly()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var sent = new float[]
            {
                123.456f, -789.012f, 0.5f, -0.0001f,
                -1.25f, 4096.75f, 0f, 60f
            };

            int length = NetplayPacket.WriteSync(buffer, 4242, sent, sent.Length);

            long frame;
            var got = new float[16];
            int count;
            bool ok = NetplayPacket.ReadSync(buffer, length, out frame, got, out count);

            Assert.IsTrue(ok);
            Assert.AreEqual(4242, frame);
            Assert.AreEqual(sent.Length, count);

            for (int i = 0; i < sent.Length; i++)
            {
                // Bit-identical, not approximately equal. Anything less and the
                // machines are still apart after the repair that was meant to bring
                // them together.
                Assert.AreEqual(sent[i], got[i], 0f, "value " + i);
            }
        }

        [TestMethod]
        public void VelocityTravelsWithPosition()
        {
            // A king put in the right place with the wrong momentum leaves again
            // immediately, and the two are apart on the very next frame. Four
            // numbers per player is the contract; two would repair only the frame
            // it arrived on.
            var buffer = new byte[NetplayPacket.MaxSize];
            var sent = new float[] { 10f, 20f, -3f, 7f };

            int length = NetplayPacket.WriteSync(buffer, 1, sent, sent.Length);

            long frame;
            var got = new float[8];
            int count;
            NetplayPacket.ReadSync(buffer, length, out frame, got, out count);

            Assert.AreEqual(-3f, got[2], 0f);
            Assert.AreEqual(7f, got[3], 0f);
        }

        [TestMethod]
        public void RejectsAPacketClaimingMoreThanArrived()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var sent = new float[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f };
            NetplayPacket.WriteSync(buffer, 1, sent, sent.Length);

            long frame;
            var got = new float[16];
            int count;

            // The header says eight values follow; ten bytes arrived. This came off
            // the network and must not be trusted into a read past the end.
            bool ok = NetplayPacket.ReadSync(buffer, 10, out frame, got, out count);

            Assert.IsFalse(ok);
            Assert.AreEqual(0, count);
        }

        [TestMethod]
        public void RejectsAPacketWantingMoreRoomThanTheReaderHas()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var sent = new float[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f };
            int length = NetplayPacket.WriteSync(buffer, 1, sent, sent.Length);

            long frame;
            var got = new float[4];
            int count;

            bool ok = NetplayPacket.ReadSync(buffer, length, out frame, got, out count);

            Assert.IsFalse(ok);
        }

        [TestMethod]
        public void ItIsSmallEnoughNeverToFragment()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var sent = new float[16];

            int length = NetplayPacket.WriteSync(buffer, 1, sent, sent.Length);

            // Four players at four numbers each, plus the kind and the frame. Well
            // under any MTU, which is what keeps one lost datagram from becoming a
            // lost burst.
            Assert.AreEqual(70, length);
            Assert.IsTrue(length <= NetplayPacket.MaxSize);
        }
    }
}
