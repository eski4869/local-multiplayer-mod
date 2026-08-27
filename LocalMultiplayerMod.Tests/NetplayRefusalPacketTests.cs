using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Telling the other side why they were refused.
    ///
    /// The reason is a number rather than a sentence on purpose: a build being
    /// refused is by definition a build that may not share this one's wording, and
    /// the commonest refusal of all is that the two builds differ.
    /// </summary>
    [TestClass]
    public class NetplayRefusalPacketTests
    {
        [TestMethod]
        public void TheReasonSurvivesTheTrip()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            int length = NetplayPacket.WriteRefused(
                buffer, NetplayPacket.RefusalReason.Level
            );

            NetplayPacket.RefusalReason reason;
            bool ok = NetplayPacket.ReadRefused(buffer, length, out reason);

            Assert.IsTrue(ok);
            Assert.AreEqual(NetplayPacket.RefusalReason.Level, reason);
        }

        [TestMethod]
        public void AReasonThisBuildDoesNotKnowIsStillARefusal()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            NetplayPacket.WriteRefused(buffer, NetplayPacket.RefusalReason.Level);

            // A later build refusing for a reason this one has never heard of.
            buffer[1] = 99;

            NetplayPacket.RefusalReason reason;
            bool ok = NetplayPacket.ReadRefused(buffer, 2, out reason);

            // Reporting "they refused, and did not say why" is the point of the
            // packet. Discarding it would put the player back where they started -
            // watching somebody arrive and leave without explanation.
            Assert.IsTrue(ok);
            Assert.AreEqual(NetplayPacket.RefusalReason.Unknown, reason);
        }

        [TestMethod]
        public void ATruncatedPacketIsNotTrusted()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            NetplayPacket.WriteRefused(buffer, NetplayPacket.RefusalReason.Protocol);

            NetplayPacket.RefusalReason reason;
            bool ok = NetplayPacket.ReadRefused(buffer, 1, out reason);

            Assert.IsFalse(ok);
        }

        [TestMethod]
        public void ItIsNotConfusedWithAnotherKind()
        {
            var buffer = new byte[NetplayPacket.MaxSize];
            var inputs = new byte[] { 1, 2, 3 };
            int length = NetplayPacket.WriteInput(buffer, 10, 0, inputs, inputs.Length);

            NetplayPacket.RefusalReason reason;
            bool ok = NetplayPacket.ReadRefused(buffer, length, out reason);

            Assert.IsFalse(ok);
        }
    }
}
