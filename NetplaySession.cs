using System;
using System.Reflection;
using HarmonyLib;
using EntityComponent;
using JumpKing;
using JumpKing.Player;
using Steamworks;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// One netplay session: who is connected, what frame it is, and whose inputs
    /// drive which player.
    ///
    /// Runs from the <c>Game1.Update</c> prefix, before the game advances anything,
    /// because a rollback has to happen before the frame it is correcting.
    ///
    /// **Interference mode**, per the owner's decision. Both players change the same
    /// world, so a wrong prediction about the remote player propagates into the
    /// local one's own outcome and has to be repaired by recomputing - which is what
    /// <see cref="RollbackBuffer"/> and <see cref="Resimulation"/> are for. The
    /// alternative, where the remote player is a ghost that cannot touch anything,
    /// needs none of that; it is not what was asked for.
    ///
    /// **Known gap: shared level state is not snapshotted yet.**
    /// <see cref="PlayerSnapshot"/> covers the players and the per-player gimmick
    /// state scoped to them, but a switch mod's own level state - which sand is
    /// solid, how far a countdown has run - lives in that mod's singletons and is
    /// not captured. A rollback therefore rewinds the players correctly and leaves
    /// the world where it was, so on a map with switch gimmicks the two can
    /// disagree after a correction. Ordinary maps are unaffected, and that is where
    /// this should be exercised first. Closing it means giving those singletons the
    /// same treatment <see cref="GimmickStateCompat"/> already gives the per-player
    /// half.
    /// </summary>
    internal sealed class NetplaySession
    {
        public enum Phase
        {
            Idle,
            WaitingForPeer,
            Handshaking,
            Playing,

            /// <summary>The peer was refused, with a reason already reported.</summary>
            Refused
        }

        /// <summary>
        /// Frames of input delay applied to the local player.
        ///
        /// Rollback removes the need for this to cover latency, so it is small: it
        /// exists to absorb the ordinary case where a packet is a frame or two late,
        /// which is cheaper to wait out than to roll back. Beyond it, rollback takes
        /// over.
        /// </summary>
        private const int InputDelayFrames = 2;

        private readonly NetplayTransport _transport = new NetplayTransport();
        private readonly NetplayClock _clock = new NetplayClock();
        private readonly InputTimeline _localInputs = new InputTimeline();
        private readonly InputTimeline _remoteInputs = new InputTimeline();
        private readonly RollbackBuffer _rollback = new RollbackBuffer();

        private readonly byte[] _sendBuffer = new byte[NetplayPacket.MaxSize];
        private readonly byte[] _packetInputs =
            new byte[InputTimeline.PacketFrames];

        private Phase _phase = Phase.Idle;
        private long _frame = -1;
        private long _lastAppliedRemote = -1;
        private float _frameDelta = 1f / NetplayClock.FramesPerSecond;
        private bool _peerPaused;
        private ulong _levelHash;

        public Phase Current
        {
            get { return _phase; }
        }

        public bool IsPlaying
        {
            get { return _phase == Phase.Playing; }
        }

        /// <summary>The frame the simulation is on. -1 before a session starts.</summary>
        public long Frame
        {
            get { return _frame; }
        }

        /// <summary>
        /// True while the peer has paused. In interference mode a shared world
        /// cannot advance without both players' input, and a pause dwarfs any
        /// prediction window, so the honest answer is to stop rather than to
        /// predict thousands of frames.
        /// </summary>
        public bool IsHeldByPeer
        {
            get { return _peerPaused; }
        }

        public void Install()
        {
            _transport.Install();
            _transport.PacketReceived += OnPacket;
            _transport.RosterChanged += OnRosterChanged;
        }

        public void Host()
        {
            if (_phase != Phase.Idle)
            {
                return;
            }

            _transport.CreateLobby();
            _phase = Phase.WaitingForPeer;
        }

        /// <summary>
        /// Opens Steam's invite picker again, for when the first one was dismissed
        /// or the overlay was not ready yet.
        /// </summary>
        public void Invite()
        {
            _transport.ShowInviteDialog();
        }

        public void Leave()
        {
            _transport.LeaveLobby();
            _phase = Phase.Idle;
            _frame = -1;
            _lastAppliedRemote = -1;
            _peerPaused = false;
            _localInputs.Reset();
            _remoteInputs.Reset();
            _rollback.Clear();
            _clock.Stop();
        }

        /// <summary>
        /// Called once per frame before the game advances.
        /// </summary>
        public void BeforeGameUpdate(float delta)
        {
            if (_phase == Phase.Idle || _phase == Phase.Refused)
            {
                return;
            }

            if (delta > 0f)
            {
                _frameDelta = delta;
            }

            _transport.Pump();

            if (_phase == Phase.WaitingForPeer || _phase == Phase.Handshaking)
            {
                SendHello();
                return;
            }

            // A paused peer stops the shared world, but through the game's own
            // pause rather than by suppressing the update - see NetplayPausePatch.
            // The frame must not advance while that holds, or the two sides would
            // disagree about how long the pause lasted.
            if (IsGamePaused)
            {
                return;
            }

            _frame++;
            _clock.NoteSimulatedFrame();
            CaptureLocalInput();
            SendInputs();
            RollBackIfMispredicted();
        }

        /// <summary>
        /// Whether the game is paused at all, from either side. Read through the
        /// game's own manager so both pauses mean the same thing - the peer's
        /// arrives here too, because <c>NetplayPausePatch</c> reports it through
        /// the same property.
        ///
        /// <c>PauseManager</c> is internal to the game, so it is reached by name.
        /// </summary>
        private static bool IsGamePaused
        {
            get
            {
                if (_pausedGetter == null)
                {
                    Type type =
                        AccessTools.TypeByName("JumpKing.PauseMenu.PauseManager");
                    _instanceField = AccessTools.Field(type, "instance");
                    _pausedGetter = AccessTools.PropertyGetter(type, "IsPaused");
                    if (_pausedGetter == null || _instanceField == null)
                    {
                        _pausedGetter = null;
                        return false;
                    }
                }

                try
                {
                    object instance = _instanceField.GetValue(null);
                    return instance != null &&
                        (bool)_pausedGetter.Invoke(instance, null);
                }
                catch
                {
                    return false;
                }
            }
        }

        private static FieldInfo _instanceField;
        private static MethodInfo _pausedGetter;

        /// <summary>
        /// Called after the game advanced, to record what the frame produced.
        /// </summary>
        public void AfterGameUpdate()
        {
            if (!IsPlaying || _frame < 0)
            {
                return;
            }

            StoreSnapshots(_frame);
            _lastAppliedRemote = _remoteInputs.ConfirmedThrough;
        }

        /// <summary>
        /// The input a player's <c>InputComponent</c> should report this frame.
        /// </summary>
        /// <returns>False when this player is not driven by the session.</returns>
        public bool TryGetInput(int playerNumber, out byte input)
        {
            input = 0;
            if (!IsPlaying || _frame < 0)
            {
                return false;
            }

            if (playerNumber == 1)
            {
                // Delayed by the same amount on both machines, so the two
                // simulations agree. Delaying only the remote side would make each
                // machine's answer depend on which player it is.
                return _localInputs.TryGet(_frame - InputDelayFrames, out input);
            }

            if (playerNumber != 2)
            {
                return false;
            }

            if (_remoteInputs.TryGet(_frame - InputDelayFrames, out input))
            {
                return true;
            }

            // Not arrived: assume they are still doing what they were doing. A full
            // charge is thirty-six frames of one button, so this is right far more
            // often here than in a game of taps - and when it is wrong, the rollback
            // above repairs it.
            input = _remoteInputs.Predict(_frame - InputDelayFrames);
            return true;
        }

        public void NoteLocalPause(bool paused)
        {
            if (!IsPlaying)
            {
                return;
            }

            int length = NetplayPacket.WriteControl(
                _sendBuffer,
                paused ? NetplayPacket.Kind.Pause : NetplayPacket.Kind.Resume
            );
            _transport.Broadcast(_sendBuffer, length);
        }

        /// <summary>
        /// Set while reading the real pad, so the patch that normally answers from
        /// the timeline steps aside. Without it the capture would read back
        /// whatever it stored last frame and the local player would never move.
        /// </summary>
        public static bool IsReadingRealPad { get; private set; }

        private void CaptureLocalInput()
        {
            PlayerContext context = MultiplayerRuntime.GetContext(1);
            if (context == null || !context.IsAlive)
            {
                return;
            }

            var input = context.Player.GetComponent<InputComponent>();
            if (input == null)
            {
                return;
            }

            InputComponent.State state;
            IsReadingRealPad = true;
            try
            {
                state = input.GetState();
            }
            finally
            {
                IsReadingRealPad = false;
            }

            _localInputs.Record(_frame, NetplayInput.Pack(state));
        }

        private void SendInputs()
        {
            int count = _localInputs.BuildPacket(_packetInputs);
            if (count <= 0)
            {
                return;
            }

            int length = NetplayPacket.WriteInput(
                _sendBuffer,
                _localInputs.HighestKnown,
                _packetInputs,
                count
            );
            _transport.Broadcast(_sendBuffer, length);
        }

        private void SendHello()
        {
            if (_transport.Peers.Count == 0)
            {
                return;
            }

            _phase = Phase.Handshaking;
            _levelHash = NetplayLevelIdentity.Current;

            int length = NetplayPacket.WriteHello(
                _sendBuffer,
                _levelHash,
                (byte)ModEntry.PlayerCount,
                true
            );
            _transport.Broadcast(_sendBuffer, length);
        }

        /// <summary>
        /// Rewinds and recomputes when what actually arrived disagrees with what
        /// was assumed.
        ///
        /// The window this can repair is <see cref="RollbackBuffer.Frames"/>. Past
        /// that the frame to return to has already been discarded, and the honest
        /// response is to carry on from where we are rather than to pretend.
        /// </summary>
        private void RollBackIfMispredicted()
        {
            long confirmed = _remoteInputs.ConfirmedThrough;
            if (confirmed <= _lastAppliedRemote)
            {
                return;
            }

            long from = _lastAppliedRemote + 1;
            if (!_rollback.CanRewindTo(from) || from > _frame)
            {
                return;
            }

            if (!RestoreAll(from))
            {
                return;
            }

            using (Resimulation.Enter())
            {
                EntityManager manager = EntityManager.instance;
                if (manager == null)
                {
                    return;
                }

                for (long frame = from; frame < _frame; frame++)
                {
                    long resumed = _frame;
                    _frame = frame;
                    manager.Update(_frameDelta);
                    StoreSnapshots(frame);
                    _frame = resumed;
                }
            }
        }

        private bool RestoreAll(long frame)
        {
            for (int number = 1; number <= MultiplayerRuntime.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                PlayerSnapshot snapshot;
                if (context == null ||
                    !_rollback.TryGet(number, frame, out snapshot) ||
                    !snapshot.Restore(context))
                {
                    return false;
                }
            }

            return true;
        }

        private void StoreSnapshots(long frame)
        {
            for (int number = 1; number <= MultiplayerRuntime.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context != null && context.IsAlive)
                {
                    _rollback.Store(
                        number,
                        frame,
                        PlayerSnapshot.Capture(context, frame)
                    );
                }
            }
        }

        private void OnRosterChanged()
        {
            if (_transport.Peers.Count == 0 && _phase == Phase.Playing)
            {
                Program.crashLog.AddErrorMessage(
                    "Local Multiplayer netplay: the peer left."
                );
                Leave();
            }
        }

        private void OnPacket(CSteamID from, byte[] payload, int length)
        {
            NetplayPacket.Kind kind;
            if (!NetplayPacket.TryReadKind(payload, length, out kind))
            {
                return;
            }

            switch (kind)
            {
                case NetplayPacket.Kind.Hello:
                    HandleHello(payload, length);
                    break;

                case NetplayPacket.Kind.Input:
                    HandleInput(payload, length);
                    break;

                case NetplayPacket.Kind.Pause:
                    _peerPaused = true;
                    break;

                case NetplayPacket.Kind.Resume:
                    _peerPaused = false;
                    break;
            }
        }

        private void HandleHello(byte[] payload, int length)
        {
            byte protocol;
            ulong levelHash;
            byte playerCount;
            bool interference;
            if (!NetplayPacket.ReadHello(
                payload,
                length,
                out protocol,
                out levelHash,
                out playerCount,
                out interference
            ))
            {
                return;
            }

            if (protocol != NetplayPacket.ProtocolVersion)
            {
                Refuse(
                    "the other player is running a different version of this mod " +
                    "(protocol " + protocol + " against " +
                    NetplayPacket.ProtocolVersion + ")"
                );
                return;
            }

            ulong mine = NetplayLevelIdentity.Current;
            if (mine == 0 || levelHash != mine)
            {
                // Named rather than left to show up as a peer standing on nothing.
                // A workshop map updates in place under one id, so "the same map"
                // is not the same claim as the same block layout.
                Refuse("the two of you are on different levels, or different versions of one");
                return;
            }

            if (_phase == Phase.Handshaking)
            {
                _phase = Phase.Playing;
                _frame = -1;
                _lastAppliedRemote = -1;
                _rollback.Clear();
                _clock.Start();

                Program.crashLog.AddErrorMessage(
                    "Local Multiplayer netplay: session started."
                );
            }
        }

        private void HandleInput(byte[] payload, int length)
        {
            long lastFrame;
            int count;
            int offset;
            if (!NetplayPacket.ReadInput(
                payload,
                length,
                out lastFrame,
                out count,
                out offset
            ))
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                _remoteInputs.Record(lastFrame - count + 1 + i, payload[offset + i]);
            }
        }

        private void Refuse(string reason)
        {
            _phase = Phase.Refused;
            Program.crashLog.AddErrorMessage(
                "Local Multiplayer netplay refused: " + reason + "."
            );
            _transport.LeaveLobby();
        }
    }
}
