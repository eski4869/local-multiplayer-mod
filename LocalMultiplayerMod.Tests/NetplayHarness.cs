using System;
using World = LocalMultiplayerMod.Tests.HarnessMachine.World;
using System.Collections.Generic;
using LocalMultiplayerMod;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Two machines playing each other, offline, in a test.
    ///
    /// The point is to stop finding these faults by asking a person to start the
    /// game on two computers. Everything that has gone wrong in this netcode was
    /// visible in one frame of arithmetic and was instead found several rounds of
    /// testing later, by somebody who had to describe it as "it feels wrong".
    ///
    /// The pieces the game supplies - a simulation, somewhere to keep snapshots -
    /// are replaced with the smallest things that behave like them. The pieces that
    /// have actually been wrong are the real ones: <see cref="Correction"/>,
    /// <see cref="RollbackPlan"/>, <see cref="RemoteInputResolver"/> and
    /// <see cref="InputTimeline"/> are the classes the game runs. A harness built
    /// on copies of them would agree with itself and prove nothing.
    /// </summary>
    internal sealed class HarnessMachine : ICorrectionWorld
    {
        /// <summary>
        /// A simulation with the one property that matters: the same states and the
        /// same inputs always give the same next state.
        ///
        /// Deliberately order-sensitive and non-commutative. A sum of inputs would
        /// hide every sequencing error in this file - replaying a frame twice, or
        /// in the wrong order, or one frame too many, would all still add up to the
        /// same number and every test would pass.
        /// </summary>
        internal struct World
        {
            public long Position;
            public long Velocity;
            public long Mix;

            /// <summary>
            /// Advances the shared world from the two players' inputs, by their
            /// identity - not by whose machine this is.
            ///
            /// Named that way on purpose. Written as "mine minus theirs" the two
            /// machines compute exact mirror images of each other and never agree,
            /// which is what this harness reported the first time it ran. That is
            /// the same confusion the session had for real when both sides called
            /// themselves player one.
            /// </summary>
            public World Step(byte playerOne, byte playerTwo, long frame)
            {
                var next = this;
                next.Velocity = Velocity + playerOne - playerTwo;
                next.Position = Position + next.Velocity;
                next.Mix = unchecked(Mix * 31 + next.Position * 7 + frame);
                return next;
            }

            public override string ToString()
            {
                return "pos=" + Position + " vel=" + Velocity + " mix=" + Mix;
            }

            public bool Matches(World other)
            {
                return Position == other.Position &&
                    Velocity == other.Velocity &&
                    Mix == other.Mix;
            }
        }

        private readonly InputTimeline _localInputs = new InputTimeline();
        private readonly InputTimeline _remoteInputs = new InputTimeline();
        private readonly InputTimeline _usedRemote = new InputTimeline();
        private readonly RemoteInputResolver _resolver;

        private readonly Dictionary<long, World> _snapshots = new Dictionary<long, World>();

        private World _world;
        private long _frame = -1;
        private long _lastApplied = -1;

        private readonly bool _isPlayerOne;

        /// <summary>
        /// Which player this machine drives. Both machines must agree, which is
        /// what the lobby owner settles in the real session.
        /// </summary>
        public HarnessMachine(bool isPlayerOne)
        {
            _isPlayerOne = isPlayerOne;
            _resolver = new RemoteInputResolver(_remoteInputs, _usedRemote);
        }

        public World State
        {
            get { return _world; }
        }

        /// <summary>The state this machine computed for a given frame.</summary>
        public bool TryGetState(long frame, out World state)
        {
            return _snapshots.TryGetValue(frame, out state);
        }

        public long Frame
        {
            get { return _frame; }
        }

        public int Corrections { get; private set; }
        public int FramesReplayed { get; private set; }
        public int Stalls { get; private set; }
        public readonly List<string> Reports = new List<string>();

        /// <summary>
        /// One frame, in the order the session runs it: advance, take this
        /// machine's own input, repair anything the newly arrived input disproved,
        /// then simulate.
        /// </summary>
        public void Tick(byte localInput)
        {
            if (ShouldStall())
            {
                Stalls++;
                return;
            }

            _frame++;
            _localInputs.Record(_frame, localInput);

            Correction.Result result = Correction.Run(this);
            if (result.Outcome == Correction.Outcome.Applied)
            {
                Corrections++;
                FramesReplayed += result.Replayed;
            }

            Simulate(_frame);
        }

        /// <summary>
        /// The one bound that still stops the game: never guess further ahead than
        /// the prediction window, because that is what caps a correction's cost.
        /// </summary>
        private bool ShouldStall()
        {
            long confirmed = _remoteInputs.ConfirmedThrough;
            return confirmed >= 0 &&
                _frame - confirmed >= RollbackPlan.MaxPredictionFrames;
        }

        private void Simulate(long frame)
        {
            long inputFrame = frame - RollbackPlan.InputDelayFrames;

            byte local;
            if (!_localInputs.TryGet(inputFrame, out local))
            {
                local = 0;
            }

            bool predicted;
            byte remote = inputFrame < 0
                ? (byte)0
                : _resolver.Resolve(inputFrame, out predicted);

            _world = _isPlayerOne
                ? _world.Step(local, remote, frame)
                : _world.Step(remote, local, frame);
            _snapshots[frame] = _world;
        }

        /// <summary>What this machine would put on the wire this frame.</summary>
        public void FillPacket(out long lastFrame, out byte[] inputs, out int count)
        {
            var buffer = new byte[InputTimeline.PacketFrames];
            count = _localInputs.BuildPacket(buffer);
            lastFrame = _localInputs.HighestKnown;
            inputs = buffer;
        }

        public void ReceivePacket(long lastFrame, byte[] inputs, int count)
        {
            _remoteInputs.Receive(lastFrame, inputs, count);
        }

        // --- ICorrectionWorld -------------------------------------------------

        long ICorrectionWorld.CurrentFrame
        {
            get { return _frame; }
        }

        long ICorrectionWorld.RemoteConfirmedThrough
        {
            get { return _remoteInputs.ConfirmedThrough; }
        }

        long ICorrectionWorld.LastAppliedRemote
        {
            get { return _lastApplied; }
            set { _lastApplied = value; }
        }

        long ICorrectionWorld.FirstWrongInputFrame(long from, long through)
        {
            return _resolver.FirstWrongInputFrame(from, through);
        }

        bool ICorrectionWorld.CanRestore(long simulationFrame)
        {
            return simulationFrame >= 0 &&
                simulationFrame < _frame &&
                simulationFrame > _frame - RollbackPlan.RequiredBufferFrames &&
                _snapshots.ContainsKey(simulationFrame);
        }

        bool ICorrectionWorld.Restore(long simulationFrame)
        {
            World stored;
            if (!_snapshots.TryGetValue(simulationFrame, out stored))
            {
                return false;
            }

            _world = stored;
            return true;
        }

        void ICorrectionWorld.ReplayFrame(long simulationFrame)
        {
            long resumed = _frame;
            _frame = simulationFrame;
            Simulate(simulationFrame);
            _frame = resumed;
        }

        void ICorrectionWorld.Report(string message)
        {
            Reports.Add(message);
        }
    }

    /// <summary>
    /// A link between two machines that delivers packets late, out of order, or not
    /// at all - deterministically, so a failure can be reproduced from its seed.
    /// </summary>
    internal sealed class HarnessLink
    {
        private struct Pending
        {
            public int DeliverAt;
            public long LastFrame;
            public byte[] Inputs;
            public int Count;
        }

        private readonly List<Pending> _inFlight = new List<Pending>();
        private readonly Random _random;
        private readonly int _latency;
        private readonly int _jitter;
        private readonly int _lossPercent;
        private int _now;

        public HarnessLink(int latencyFrames, int jitterFrames, int lossPercent, int seed)
        {
            _latency = latencyFrames;
            _jitter = jitterFrames;
            _lossPercent = lossPercent;
            _random = new Random(seed);
        }

        public void Send(long lastFrame, byte[] inputs, int count)
        {
            // Dropped outright. Every packet carries sixteen frames of history, so
            // losing one is meant to be survivable - this is what checks that it is.
            if (_lossPercent > 0 && _random.Next(100) < _lossPercent)
            {
                return;
            }

            int delay = _latency;
            if (_jitter > 0)
            {
                delay += _random.Next(-_jitter, _jitter + 1);
            }

            if (delay < 1)
            {
                delay = 1;
            }

            _inFlight.Add(new Pending
            {
                DeliverAt = _now + delay,
                LastFrame = lastFrame,
                Inputs = (byte[])inputs.Clone(),
                Count = count
            });
        }

        /// <summary>Hands over everything that has arrived by now.</summary>
        public void Advance(HarnessMachine to)
        {
            _now++;

            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                if (_inFlight[i].DeliverAt > _now)
                {
                    continue;
                }

                Pending p = _inFlight[i];
                _inFlight.RemoveAt(i);
                to.ReceivePacket(p.LastFrame, p.Inputs, p.Count);
            }
        }
    }

    /// <summary>Runs the two machines against each other for a while.</summary>
    internal static class Harness
    {
        public sealed class Outcome
        {
            public HarnessMachine A;
            public HarnessMachine B;
            public long ComparedAtFrame;
            public bool Converged;
            public string Detail;
        }

        /// <summary>
        /// Plays <paramref name="frames"/> real frames, then lets the two settle and
        /// compares them at the newest frame both have reached.
        /// </summary>
        public static Outcome Play(
            int frames,
            int latency,
            int jitter,
            int lossPercent,
            int seed
        )
        {
            var a = new HarnessMachine(true);
            var b = new HarnessMachine(false);
            var aToB = new HarnessLink(latency, jitter, lossPercent, seed);
            var bToA = new HarnessLink(latency, jitter, lossPercent, seed + 977);

            var inputs = new Random(seed + 31);
            byte aHeld = 0;
            byte bHeld = 0;
            int aHold = 0;
            int bHold = 0;

            for (int i = 0; i < frames; i++)
            {
                // Held for stretches, like a charge is. Uniform noise would make
                // every prediction wrong and exercise a case this game never has.
                if (aHold-- <= 0)
                {
                    aHeld = (byte)inputs.Next(0, 4);
                    aHold = inputs.Next(3, 30);
                }

                if (bHold-- <= 0)
                {
                    bHeld = (byte)inputs.Next(0, 4);
                    bHold = inputs.Next(3, 30);
                }

                aToB.Advance(b);
                bToA.Advance(a);

                a.Tick(aHeld);
                b.Tick(bHeld);

                SendFrom(a, aToB);
                SendFrom(b, bToA);
            }

            // Let everything in flight land and every outstanding correction run,
            // with no new input generated: convergence is a claim about the frames
            // both machines have finished, not about the ones still arriving.
            for (int i = 0; i < 240; i++)
            {
                aToB.Advance(b);
                bToA.Advance(a);
                a.Tick(aHeld);
                b.Tick(bHeld);
                SendFrom(a, aToB);
                SendFrom(b, bToA);
            }

            var outcome = new Outcome();
            outcome.A = a;
            outcome.B = b;

            // Compared at a frame, not at a moment.
            //
            // The two machines are not required to be on the same frame number at
            // the same instant, and requiring it is a misreading this harness made
            // first time out: it reported a break where the states were identical
            // and one machine was simply a frame further on, having stalled once
            // less. Each side stalls independently by design.
            //
            // What synchronisation actually means is that frame N is the same state
            // on both, whenever each of them gets there. Backed off far enough that
            // every correction for that frame has certainly run.
            long common = Math.Min(a.Frame, b.Frame) -
                RollbackPlan.MaxPredictionFrames - RollbackPlan.InputDelayFrames;
            outcome.ComparedAtFrame = common;

            World stateA;
            World stateB;
            bool haveA = a.TryGetState(common, out stateA);
            bool haveB = b.TryGetState(common, out stateB);

            outcome.Converged = haveA && haveB && stateA.Matches(stateB);
            outcome.Detail =
                "at f=" + common +
                ": A " + (haveA ? stateA.ToString() : "missing") +
                " | B " + (haveB ? stateB.ToString() : "missing") +
                " (A ended f=" + a.Frame + ", B ended f=" + b.Frame + ")";
            return outcome;
        }

        private static void SendFrom(HarnessMachine from, HarnessLink link)
        {
            long lastFrame;
            byte[] inputs;
            int count;
            from.FillPacket(out lastFrame, out inputs, out count);
            if (count > 0)
            {
                link.Send(lastFrame, inputs, count);
            }
        }
    }
}
