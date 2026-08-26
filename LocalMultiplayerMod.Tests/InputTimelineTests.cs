using LocalMultiplayerMod;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Covers the property the whole transport rests on: a lost packet costs
    /// nothing, because the next one carries what it held.
    ///
    /// That is what buys the right to send unreliable and unordered, and skipping
    /// the reliable send's buffering is not a detail - it can hold a packet for up
    /// to 200ms, and rollback wants freshness over completeness. If the overlap does
    /// not actually heal, the symptom is a remote player who stutters only under
    /// packet loss, which is the hardest condition to reproduce deliberately.
    /// </summary>
    [TestClass]
    public class InputTimelineTests
    {
        private const byte Left = 1;
        private const byte Right = 2;
        private const byte Jump = 4;

        private static InputTimeline Filled(int frames, byte input)
        {
            var timeline = new InputTimeline();
            for (int f = 0; f < frames; f++)
            {
                timeline.Record(f, input);
            }

            return timeline;
        }

        [TestMethod]
        public void ReadsBackWhatWasRecorded()
        {
            var timeline = new InputTimeline();
            timeline.Record(0, Left);
            timeline.Record(1, Jump);

            byte input;
            Assert.IsTrue(timeline.TryGet(0, out input));
            Assert.AreEqual(Left, input);
            Assert.IsTrue(timeline.TryGet(1, out input));
            Assert.AreEqual(Jump, input);
        }

        [TestMethod]
        public void ConfirmsThroughTheLastContiguousFrame()
        {
            var timeline = new InputTimeline();
            timeline.Record(0, Left);
            timeline.Record(1, Left);
            timeline.Record(3, Left);

            // Frame 2 is missing, so 3 is known but not confirmed. A rollback may
            // only rewind to a frame nothing is still predicted for.
            Assert.AreEqual(3, timeline.HighestKnown);
            Assert.AreEqual(1, timeline.ConfirmedThrough);
        }

        [TestMethod]
        public void FillingAHoleAdvancesTheConfirmedFrame()
        {
            var timeline = new InputTimeline();
            timeline.Record(0, Left);
            timeline.Record(3, Left);
            timeline.Record(1, Left);
            timeline.Record(2, Left);

            Assert.AreEqual(3, timeline.ConfirmedThrough);
        }

        [TestMethod]
        public void APacketCarriesTheLastSixteenFrames()
        {
            InputTimeline timeline = Filled(40, Right);
            var buffer = new byte[InputTimeline.PacketFrames];

            int count = timeline.BuildPacket(buffer);

            Assert.AreEqual(InputTimeline.PacketFrames, count);
        }

        [TestMethod]
        public void AShortSessionSendsOnlyWhatExists()
        {
            InputTimeline timeline = Filled(3, Right);
            var buffer = new byte[InputTimeline.PacketFrames];

            Assert.AreEqual(3, timeline.BuildPacket(buffer));
        }

        [TestMethod]
        public void ADroppedPacketIsHealedByTheNextOne()
        {
            var sender = new InputTimeline();
            var receiver = new InputTimeline();
            var buffer = new byte[InputTimeline.PacketFrames];

            for (int frame = 0; frame < 40; frame++)
            {
                sender.Record(frame, Jump);
                int count = sender.BuildPacket(buffer);

                // Every third packet never arrives.
                if (frame % 3 != 0)
                {
                    receiver.Receive(frame, buffer, count);
                }
            }

            // No hole survives below the newest arrival, because each packet
            // re-sent the fifteen frames before it. Frame 39's own packet was one
            // of the dropped ones, so 38 is the newest that could have arrived -
            // and the next packet would carry 39 too.
            Assert.AreEqual(38, receiver.ConfirmedThrough);
            Assert.AreEqual(38, receiver.HighestKnown);
        }

        [TestMethod]
        public void SixteenConsecutiveLossesLeaveAHole()
        {
            var sender = new InputTimeline();
            var receiver = new InputTimeline();
            var buffer = new byte[InputTimeline.PacketFrames];

            for (int frame = 0; frame < 40; frame++)
            {
                sender.Record(frame, Jump);
                int count = sender.BuildPacket(buffer);
                if (frame < 5 || frame >= 25)
                {
                    receiver.Receive(frame, buffer, count);
                }
            }

            // The stated limit of the scheme, asserted rather than assumed: twenty
            // consecutive losses is more than the overlap can cover.
            Assert.AreEqual(4, receiver.ConfirmedThrough);
            Assert.AreEqual(39, receiver.HighestKnown);
        }

        [TestMethod]
        public void PacketsArrivingOutOfOrderStillConfirm()
        {
            var sender = new InputTimeline();
            var receiver = new InputTimeline();
            var first = new byte[InputTimeline.PacketFrames];
            var second = new byte[InputTimeline.PacketFrames];

            for (int frame = 0; frame <= 5; frame++)
            {
                sender.Record(frame, Left);
            }

            int firstCount = sender.BuildPacket(first);

            for (int frame = 6; frame <= 10; frame++)
            {
                sender.Record(frame, Right);
            }

            int secondCount = sender.BuildPacket(second);

            // Newest first: unordered delivery is expected, not an error case.
            receiver.Receive(10, second, secondCount);
            receiver.Receive(5, first, firstCount);

            Assert.AreEqual(10, receiver.ConfirmedThrough);

            byte input;
            Assert.IsTrue(receiver.TryGet(10, out input));
            Assert.AreEqual(Right, input);
        }

        [TestMethod]
        public void PredictsByRepeatingTheLastKnownInput()
        {
            var timeline = new InputTimeline();
            timeline.Record(0, Jump);
            timeline.Record(1, Jump);

            // A full charge is thirty-six frames of the same button, so holding the
            // previous input is right far more often here than in a game of taps.
            Assert.AreEqual(Jump, timeline.Predict(5));
        }

        [TestMethod]
        public void PredictsNothingBeforeAnyInputArrives()
        {
            var timeline = new InputTimeline();

            Assert.AreEqual(0, timeline.Predict(0));
        }

        [TestMethod]
        public void AGapInRecordingDoesNotReadStaleFramesFromAPreviousLap()
        {
            var timeline = new InputTimeline();
            for (int frame = 0; frame < 300; frame++)
            {
                timeline.Record(frame, Jump);
            }

            // Far enough ahead to land on ring slots a previous lap already used.
            timeline.Record(400, Left);

            byte input;
            Assert.IsFalse(timeline.TryGet(350, out input));
            Assert.IsTrue(timeline.TryGet(400, out input));
            Assert.AreEqual(Left, input);
        }

        [TestMethod]
        public void RejectsAFrameThatHasScrolledOutOfRange()
        {
            var timeline = new InputTimeline();
            for (int frame = 0; frame < 300; frame++)
            {
                timeline.Record(frame, Jump);
            }

            Assert.IsFalse(timeline.Record(1, Left));
        }

        [TestMethod]
        public void JoiningLateLeavesAHoleNothingCanFill()
        {
            var sender = new InputTimeline();
            var receiver = new InputTimeline();
            var buffer = new byte[InputTimeline.PacketFrames];

            for (int frame = 0; frame <= 40; frame++)
            {
                sender.Record(frame, Jump);
            }

            // Only the newest packet is heard, as it would be by a receiver that
            // started listening at frame forty.
            receiver.Receive(40, buffer, sender.BuildPacket(buffer));

            // Confirmation is contiguity from the beginning, so the missing
            // opening frames hold it at -1 permanently. Anything that only acts
            // when the confirmed frame advances - the rollback did - then never
            // acts at all, for the rest of the session.
            //
            // Which is why the session records remote input from the moment it
            // arrives rather than discarding it until the origin is settled.
            Assert.AreEqual(-1, receiver.ConfirmedThrough);
            Assert.AreEqual(40, receiver.HighestKnown);
        }

        [TestMethod]
        public void ConfirmedRunsPastAMachineThatIsBehind()
        {
            var timeline = new InputTimeline();
            for (int frame = 0; frame <= 40; frame++)
            {
                timeline.Record(frame, Jump);
            }

            // A machine simulating frame ten holds confirmed input up to forty:
            // the packets describe frames it has not reached. A marker for "how
            // far the guesses have been checked" must be held to the current
            // frame rather than following this, or every misprediction at the
            // present falls below the mark and is never examined again.
            Assert.AreEqual(40, timeline.ConfirmedThrough);
        }

        [TestMethod]
        public void ResetClearsEverything()
        {
            InputTimeline timeline = Filled(10, Jump);

            timeline.Reset();

            byte input;
            Assert.AreEqual(-1, timeline.HighestKnown);
            Assert.AreEqual(-1, timeline.ConfirmedThrough);
            Assert.IsFalse(timeline.TryGet(5, out input));
        }
    }
}
