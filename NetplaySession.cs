using System;
using System.Reflection;
using HarmonyLib;
using EntityComponent;
using JumpKing;
using JumpKing.Player;
using Microsoft.Xna.Framework;
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

            /// <summary>A lobby exists and nobody else is in it yet.</summary>
            WaitingForPeer,

            /// <summary>
            /// In the lobby, but on a different level than the session is being
            /// played on.
            ///
            /// This exists because joining and connecting are not the same step,
            /// which was the assumption that made the invited side impossible to
            /// use. Accepting an invitation puts somebody in a lobby; it cannot put
            /// them in the right level, and a level change restarts the run. So a
            /// joiner sits here, is told which level to load, and connects once
            /// they are on it. Lobby membership outlives the level change, which is
            /// what makes the wait usable rather than a dead end.
            /// </summary>
            NeedsLevel,

            Handshaking,
            Playing,

            /// <summary>The peer was refused, with a reason already reported.</summary>
            Refused
        }

        /// <summary>
        /// What the host writes on the lobby so a joiner can see what they are
        /// joining before any packet is exchanged.
        ///
        /// A lobby is a key/value store every member can read, and it is the only
        /// thing an invited player has when they accept. Without it they hold a
        /// lobby id and nothing else: not which level, not whether battle is on,
        /// not whether their build even matches. Then a mismatch can only be
        /// reported as a refusal with nothing to act on.
        /// </summary>
        private static class LobbyKeys
        {
            public const string Protocol = "protocol";
            public const string LevelHash = "level_hash";
            public const string LevelId = "level_id";
            public const string Battle = "battle";
        }

        /// <summary>
        /// The player this machine's pad drives, and the one the wire drives.
        ///
        /// **The lobby owner is player one on both machines.** Both sides must agree
        /// which body is which, and the owner is the one fact Steam guarantees they
        /// read identically.
        ///
        /// The alternative - each machine calling its own player one - makes the two
        /// mirror images of each other, and that only holds while the two slots are
        /// interchangeable. They are not. Slot one carries the block behaviours the
        /// mods themselves registered; slot two carries copies of them. Under the
        /// mirror the same character therefore ran an original on one machine and a
        /// copy on the other, and any difference between the two is a divergence
        /// with nothing to correct it.
        ///
        /// Slot one also owns the base game's save path, which is another thing
        /// that cannot be true of a different player on each machine.
        ///
        /// The mirror was adopted to stop a guest watching the host's camera. That
        /// was a real fault and this is not a return to it: the camera follows
        /// whoever this machine is driving, which is
        /// <see cref="PlayerContext.IsLocallyDriven"/> and has nothing to do with
        /// the slot number. Mixing the two was the mistake - a presentation problem
        /// fixed by moving simulation identity.
        /// </summary>
        private int LocalPlayer
        {
            get { return _transport.IsLobbyOwner ? 1 : 2; }
        }

        private int RemotePlayer
        {
            get { return _transport.IsLobbyOwner ? 2 : 1; }
        }

        /// <summary>Which slot this machine drives. For the camera to follow.</summary>
        public int LocalPlayerNumber
        {
            get { return LocalPlayer; }
        }

        /// <summary>
        /// Frames of input delay, applied identically on both machines.
        ///
        /// This is standard practice and was dropped once, on the reasoning that
        /// rollback made it unnecessary. It does not: the two are used together.
        /// Delay is what makes the peer's input usually arrive before the frame
        /// that needs it, so prediction becomes the exception rather than every
        /// single frame - measured at 240 guessed frames a second against zero real
        /// ones with no delay at all. Rollback then covers what the delay does not.
        ///
        /// Kept small, because delay is felt directly. Two frames is about 33ms,
        /// which buys most of the benefit before becoming the thing complained
        /// about.
        /// </summary>
        private const int InputDelayFrames = 2;

        /// <summary>
        /// The frame whose inputs the simulation is running.
        ///
        /// One place on purpose. A delay applied by some readers and not others is
        /// a divergence between two machines that both believe they applied it, and
        /// nothing downstream could tell them apart.
        /// </summary>
        private long SimulatedInputFrame
        {
            get { return _frame - InputDelayFrames; }
        }

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
            _transport.LobbyEntered += OnLobbyEntered;
            _transport.SearchFinished += OnSearchFinished;
        }

        /// <summary>The level a joiner still has to load, or null.</summary>
        public string RequiredLevelId { get; private set; }

        /// <summary>
        /// Whether this machine opened the session. Worth saying out loud in the
        /// menu: the two sides do different things - one invites, the other waits
        /// on a level - and a line that reads the same on both leaves nobody sure
        /// which they are.
        /// </summary>
        public bool IsHost
        {
            get { return _transport.IsLobbyOwner; }
        }

        /// <summary>Who is on the other end, or null.</summary>
        public string PeerName
        {
            get { return _transport.PeerName; }
        }

        /// <summary>
        /// Brings the second player into the world, and takes them out again.
        ///
        /// Deliberately not done when the lobby opens. A lobby with nobody in it
        /// has no second player, and spawning one there puts a motionless king on
        /// screen before anything is connected - which says the session has started
        /// when it has not. The body appears when somebody is actually driving it.
        ///
        /// Changing the player count mid-run is an ordinary operation here: the
        /// pause menu has always been able to turn local multiplayer on during a
        /// run, and this is the same path.
        /// </summary>
        private void SetSecondPlayerPresent(bool present)
        {
            if (present == ModEntry.IsMultiplayerEnabled)
            {
                return;
            }

            ModEntry.SetPlayerMode(
                present ? 2 : 1,
                present ? TwoPlayerLayout.Shared : ModEntry.TwoPlayerLayout
            );
        }

        /// <summary>
        /// Either side entering a lobby. The creator describes the session; anyone
        /// else reads that description and works out whether they can join yet.
        /// </summary>
        private void OnLobbyEntered(bool created)
        {
            if (created)
            {
                _transport.WriteLobbyData(
                    LobbyKeys.Protocol,
                    NetplayPacket.ProtocolVersion.ToString()
                );
                _transport.WriteLobbyData(
                    LobbyKeys.LevelHash,
                    NetplayLevelIdentity.Current.ToString()
                );
                _transport.WriteLobbyData(
                    LobbyKeys.LevelId,
                    NetplayLevelIdentity.CurrentId ?? string.Empty
                );
                _transport.WriteLobbyData(
                    LobbyKeys.Battle,
                    ModEntry.IsBattleMode ? "1" : "0"
                );

                // Marks the lobby as this mod's, so a guest searching finds only
                // sessions they can actually join.
                _transport.WriteLobbyData(
                    NetplayTransport.LobbyTag,
                    NetplayTransport.LobbyTagValue
                );

                _phase = Phase.WaitingForPeer;
                NetplayNotice.Show("lobby open - invite a friend to join");
                return;
            }

            JoinExistingLobby();
        }

        private void JoinExistingLobby()
        {
            string protocol = _transport.ReadLobbyData(LobbyKeys.Protocol);
            if (protocol != NetplayPacket.ProtocolVersion.ToString())
            {
                Refuse(
                    "the host is running a different version of this mod " +
                    "(protocol " + protocol + " against " +
                    NetplayPacket.ProtocolVersion + ")"
                );
                return;
            }

            // Battle is the host's to decide and costs nothing to match early. The
            // player count deliberately is not touched here - see AddSecondPlayer.
            ModEntry.SetBattleMode(
                _transport.ReadLobbyData(LobbyKeys.Battle) == "1"
            );

            RequiredLevelId = _transport.ReadLobbyData(LobbyKeys.LevelId);
            AdvanceIfLevelMatches();
        }

        /// <summary>
        /// Called when a level finishes loading, so a joiner who was waiting on the
        /// right one picks up from there.
        /// </summary>
        public void OnLevelStarted()
        {
            NetplayLevelIdentity.Invalidate();

            if (_phase == Phase.NeedsLevel)
            {
                AdvanceIfLevelMatches();
                return;
            }

            // The host changing level changes what joiners have to load, so the
            // description has to change with it. A lobby still advertising the
            // previous level would send somebody to the wrong one.
            if (_phase == Phase.WaitingForPeer)
            {
                _transport.WriteLobbyData(
                    LobbyKeys.LevelHash,
                    NetplayLevelIdentity.Current.ToString()
                );
                _transport.WriteLobbyData(
                    LobbyKeys.LevelId,
                    NetplayLevelIdentity.CurrentId ?? string.Empty
                );
            }
        }

        private void AdvanceIfLevelMatches()
        {
            string wanted = _transport.ReadLobbyData(LobbyKeys.LevelHash);
            ulong mine = NetplayLevelIdentity.Current;

            if (!string.IsNullOrEmpty(wanted) && wanted == mine.ToString() &&
                mine != 0)
            {
                _phase = Phase.Handshaking;
                return;
            }

            // Named rather than refused. A joiner who is simply on the wrong level
            // has something to do about it, and saying so is the difference between
            // a wait and a dead end.
            if (_phase != Phase.NeedsLevel)
            {
                NetplayNotice.Show(
                    string.IsNullOrEmpty(RequiredLevelId)
                        ? "joined - load the host's level to start"
                        : "joined - load level " + RequiredLevelId + " to start"
                );
            }

            _phase = Phase.NeedsLevel;
        }

        public void Host()
        {
            if (_phase != Phase.Idle)
            {
                return;
            }

            // The phase is set when the lobby actually exists, not here: creating
            // one can fail, and claiming to be waiting for a peer when there is no
            // lobby to wait in would be a lie the menu then repeats.
            _transport.CreateLobby();
        }

        /// <summary>
        /// Looks for a lobby to join, without waiting to be invited.
        ///
        /// An invitation puts the guest at the host's mercy for even getting in.
        /// A friends-only lobby is visible to friends, so it can simply be found.
        /// </summary>
        public void Join()
        {
            if (_phase != Phase.Idle)
            {
                return;
            }

            _searching = true;
            NetplayNotice.Show("looking for a friend's lobby");
            _transport.FindLobbies();
        }

        /// <summary>True while a search is out. The menu says so rather than
        /// appearing to have done nothing.</summary>
        public bool IsSearching
        {
            get { return _searching; }
        }

        private bool _searching;

        private void OnSearchFinished(int found)
        {
            _searching = false;
            _selected = 0;

            NetplayNotice.Show(
                found == 0
                    ? "no lobbies found"
                    : found + (found == 1 ? " lobby found" : " lobbies found")
            );
        }

        /// <summary>The lobbies the last search turned up.</summary>
        public System.Collections.Generic.IList<NetplayTransport.FoundLobby> Found
        {
            get { return _transport.Found; }
        }

        /// <summary>Which one the menu is pointing at.</summary>
        public int Selected
        {
            get { return _selected; }
        }

        private int _selected;

        public void SelectNext(int step)
        {
            int count = _transport.Found.Count;
            if (count == 0)
            {
                return;
            }

            _selected = ((_selected + step) % count + count) % count;
        }

        /// <summary>Joins the lobby the menu is pointing at.</summary>
        public void JoinSelected()
        {
            if (_phase != Phase.Idle || _transport.Found.Count == 0)
            {
                return;
            }

            _transport.JoinFound(_selected);
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
            if (_phase != Phase.Idle)
            {
                NetplayNotice.Show(
                    _transport.IsLobbyOwner
                        ? "lobby closed"
                        : "left the lobby"
                );
            }

            _transport.LeaveLobby();

            // The second body goes with the session that justified it.
            SetSecondPlayerPresent(false);

            _phase = Phase.Idle;
            _frame = -1;
            _lastAppliedRemote = -1;
            _startFrame = -1;
            _peerFrame = -1;

            // Left set, this would hold the world still after the session that
            // justified it has gone.
            _stallThisFrame = false;
            _consecutiveStalls = 0;
            _peerPaused = false;
            RequiredLevelId = null;
            _localInputs.Reset();
            _remoteInputs.Reset();
            _usedRemote.Reset();
            _rollback.Clear();
            _clock.Stop();
        }

        /// <summary>
        /// Called once per frame before the game advances.
        /// </summary>
        public void BeforeGameUpdate(float delta)
        {
            // Cleared before anything can return early. Every path out of here
            // still reaches EntityManager.Update afterwards, so a flag left set
            // from an earlier frame would hold the world still on a frame that was
            // never asked to wait - including after the session has ended.
            _stallThisFrame = false;

            if (_phase == Phase.Idle || _phase == Phase.Refused)
            {
                return;
            }

            if (delta > 0f)
            {
                _frameDelta = delta;
            }

            _transport.Pump();

            // Waiting on the player to load the right level. The lobby membership
            // is doing its job; there is nothing to send until they are on it.
            if (_phase == Phase.NeedsLevel)
            {
                return;
            }

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

            // Nothing may advance before the origin is known. A guest that started
            // counting on its own would be a different number of frames into the
            // simulation than the host for the same frame number, which is what
            // made the two drift by one step of gravity and then by everything.
            if (_startFrame < 0)
            {
                return;
            }

            // Holding back the machine that is ahead is the only thing that can
            // close a gap that keeps growing, and it was attempted here, and here
            // is the wrong place. Returning early stops this bookkeeping and does
            // not stop the game: physics still advanced, no input was captured for
            // the frame that had just happened, and the local player was handed the
            // previous frame's input again - so their own king moved on a repeat of
            // whatever they last pressed.
            //
            // A player will forgive the other one stuttering. Their own controls
            // coming apart is the one thing they will not.
            //
            // So it holds the simulation as well, through NetplayStallPatch, and
            // the two happen together: no frame advances and no frame is
            // simulated. The game is fixed-timestep - every update is exactly one
            // sixtieth of a second of game time whatever the hardware manages - so
            // frame N is the same state on both machines and a slower one only
            // arrives there later. Nothing about the simulation differs with the
            // hardware; only when each machine reaches it does, and the one in
            // front waiting is the only thing that closes that.
            _stallThisFrame = ShouldWaitForPeer();
            if (_stallThisFrame)
            {
                _stallReport++;
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

            TraceFrame();
            ReportCost();

            if (_frame % ChecksumInterval == 0)
            {
                // Kept so the peer's digest for the same frame has something to be
                // compared against when it arrives.
                _checksums[_frame] = ComputeChecksum();
                SendChecksum();

                // Bounded: only the recent ones can still be answered.
                if (_checksums.Count > 16)
                {
                    foreach (long old in new System.Collections.Generic.List<long>(
                        _checksums.Keys
                    ))
                    {
                        if (old < _frame - ChecksumInterval * 8)
                        {
                            _checksums.Remove(old);
                        }
                    }
                }
            }

            // _lastAppliedRemote is deliberately not advanced here. It marks how
            // far the guesses have been checked, and moving it forward every frame
            // - which this used to do - told the rollback that everything had
            // already been verified. Its first test then always passed and it never
            // ran once: the remote player was driven by prediction alone, with no
            // correction ever applied.
            //
            // Walking survived that, because "the same as last frame" is usually
            // right. A charge did not: once a held jump was predicted, it was
            // predicted for ever, and the king stayed crouched.
        }

        /// <summary>
        /// The edge: buttons that went down on this frame and were not down on the
        /// one before.
        ///
        /// <c>GetState</c> and <c>GetPressedState</c> are different questions -
        /// "held" against "just pressed" - and answering both with the held state
        /// breaks every consumer that watches for the moment a charge starts or
        /// ends. Derived from the timeline rather than sent, because a frame's
        /// predecessor is already there: it costs no extra bits, and it stays
        /// correct during a re-simulation, where the real pad's own edge detection
        /// would report the live frame instead of the one being replayed.
        /// </summary>
        public bool TryGetPressedInput(int playerNumber, out byte pressed)
        {
            pressed = 0;

            byte now;
            if (!TryGetInput(playerNumber, out now))
            {
                return false;
            }

            byte before;
            if (!TryGetInputAt(playerNumber, SimulatedInputFrame - 1, out before))
            {
                before = 0;
            }

            pressed = (byte)(now & ~before);
            return true;
        }

        private bool TryGetInputAt(int playerNumber, long frame, out byte input)
        {
            input = 0;
            if (frame < 0)
            {
                return false;
            }

            if (playerNumber == LocalPlayer)
            {
                return _localInputs.TryGet(frame, out input);
            }

            if (playerNumber != RemotePlayer)
            {
                return false;
            }

            // What the simulation was given for that frame, not what later turned
            // out to be true: an edge has to be consistent with the frame that was
            // actually run, or a rollback would be comparing against a history that
            // never happened.
            return _usedRemote.TryGet(frame, out input) ||
                _remoteInputs.TryGet(frame, out input);
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

            long at = SimulatedInputFrame;

            if (playerNumber == LocalPlayer)
            {
                return _localInputs.TryGet(at, out input);
            }

            if (playerNumber != RemotePlayer)
            {
                return false;
            }

            if (!_remoteInputs.TryGet(at, out input))
            {
                // Not arrived: assume they are still doing what they were doing. A
                // full charge is thirty-six frames of one button, so this is right
                // far more often here than in a game of taps - and when it is
                // wrong, the rollback repairs it.
                input = _remoteInputs.Predict(_frame);
                _predictedFrames++;
            }
            else
            {
                _realFrames++;
            }

            // Recorded whether guessed or real, because the only way to know a
            // guess was wrong later is to have kept what was used.
            _usedRemote.Record(at, input);
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
            // Whichever body this machine's pad drives, which is not always
            // player one.
            PlayerContext context = MultiplayerRuntime.GetContext(LocalPlayer);
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
        /// <summary>
        /// Recomputes the frames a wrong guess spoiled - and only those.
        ///
        /// The version this replaces rolled back whenever new input had arrived,
        /// which is every frame: it never asked whether the guess had been wrong.
        /// With the peer a few frames away that meant re-simulating the whole world
        /// several times per frame, plus a reflection-based snapshot of every player
        /// for each one. The game then ran at a fraction of speed, which reads as
        /// network lag and as heavy controls and is neither.
        ///
        /// A prediction here is "they are still doing what they were doing", and in
        /// this game it is usually right - a full charge is thirty-six frames of one
        /// button. So the common case must cost nothing, and it now does: the frames
        /// are compared, and identical ones are simply marked as confirmed.
        /// </summary>
        private void RollBackIfMispredicted()
        {
            // Never past the frame actually simulated. Confirmed input runs ahead
            // of the simulation whenever this machine is behind the peer - the
            // packets describe frames it has not reached yet - and those frames
            // have no guess to be wrong about. Letting the marker follow the
            // confirmed frame therefore pushed it past the present, and every
            // misprediction at or before the current frame fell below the range
            // this scans and was never examined again.
            //
            // That is the whole of "one side renders the other correctly and the
            // other does not": the machine that lags is the one that stops
            // correcting, and it is the only one that lags.
            long confirmed = Math.Min(_remoteInputs.ConfirmedThrough, _frame);
            if (confirmed <= _lastAppliedRemote)
            {
                return;
            }

            long from = FirstWrongFrame(_lastAppliedRemote + 1, confirmed);
            if (from < 0)
            {
                // Every guess held. Nothing to redo.
                _lastAppliedRemote = confirmed;
                return;
            }

            if (!_rollback.CanRewindTo(from) || from > _frame)
            {
                // Too far back to repair. Carrying on from here is wrong, but it is
                // the only thing left, and saying so beats drifting in silence.
                NetplayNotice.Show("desynchronised - the correction arrived too late");
                _lastAppliedRemote = confirmed;
                return;
            }

            if (!RestoreAll(from))
            {
                NetplayNotice.Show("desynchronised - could not rewind");
                _lastAppliedRemote = confirmed;
                return;
            }

            // Bounded so one bad stretch cannot stall the game outright. Past this
            // the correction is abandoned and the drift reported, which is worse
            // than a rollback and much better than a frame that takes a second.
            if (_frame - from > MaxRollbackFrames)
            {
                NetplayNotice.Show(
                    "correction skipped - " + (_frame - from) + " frames behind"
                );
                _lastAppliedRemote = confirmed;
                return;
            }

            _rollbackCount++;
            _resimulatedFrames += _frame - from;
            var timer = System.Diagnostics.Stopwatch.StartNew();

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

                    // Cleared so the replayed frames record what they are now given
                    // rather than keeping the guess that was just proven wrong -
                    // otherwise the next comparison finds the same disagreement
                    // again and rolls back to the same place for ever.
                    _usedRemote.Record(frame, ResolveRemoteInput(frame));

                    manager.Update(_frameDelta);
                    StoreSnapshots(frame);
                    _frame = resumed;
                }
            }

            _resimulateMilliseconds += timer.Elapsed.TotalMilliseconds;
            _lastAppliedRemote = confirmed;
        }

        /// <summary>
        /// How far back a correction may reach. Every frame in the range is the
        /// whole world simulated again inside one real frame, so this is a cost
        /// ceiling before it is anything else.
        /// </summary>
        private const int MaxRollbackFrames = 20;

        private int _rollbackCount;
        private long _resimulatedFrames;
        private double _resimulateMilliseconds;
        private int _predictedFrames;
        private int _realFrames;
        private int _snapshotCount;
        private double _snapshotMilliseconds;

        /// <summary>
        /// Reports what corrections actually cost, once a second.
        ///
        /// The lag appeared the moment corrections began running, and that could be
        /// the re-simulation, the snapshots, or how often either happens. Those
        /// call for different answers, and guessing between them has cost enough
        /// rounds already.
        /// </summary>
        private void ReportCost()
        {
            if (_frame < 0 || _frame % NetplayClock.FramesPerSecond != 0)
            {
                return;
            }

            // predicted against real is what tells three very different situations
            // apart, all of which show as no rollbacks: the guesses were right, the
            // guesses were never needed because the peer's input always arrived
            // first, or the check that finds a wrong guess is broken. Only the last
            // is a fault, and reasoning cannot separate them.
            //
            // lag is how far the confirmed input trails the frame being simulated -
            // the amount this machine has to guess across, and which side is ahead.
            JumpKing.Program.crashLog.AddErrorMessage(
                "netplay cost: f=" + _frame +
                " rollbacks=" + _rollbackCount +
                " resimulated=" + _resimulatedFrames +
                " resim_ms=" + _resimulateMilliseconds.ToString("F1") +
                " snapshots=" + _snapshotCount +
                " snapshot_ms=" + _snapshotMilliseconds.ToString("F1") +
                " predicted=" + _predictedFrames +
                " real=" + _realFrames +
                " lag=" + (_frame - _remoteInputs.ConfirmedThrough) +
                " applied=" + (_frame - _lastAppliedRemote) +
                " ahead=" + (_peerFrame < 0 ? 0 : _frame - _peerFrame) +
                " stalled=" + _stallReport
            );

            _stallReport = 0;
            _predictedFrames = 0;
            _realFrames = 0;
            _rollbackCount = 0;
            _resimulatedFrames = 0;
            _resimulateMilliseconds = 0;
            _snapshotCount = 0;
            _snapshotMilliseconds = 0;
        }

        /// <summary>
        /// Writes down every reference a snapshot does not follow, once per
        /// session.
        ///
        /// This list is the shape of a whole class of bug: mutable state reachable
        /// from a player that a rollback leaves untouched, so a corrected frame is
        /// recomputed from something that was never rewound. It shows up as one
        /// specific thing going wrong at one specific moment, which is the hardest
        /// kind to trace back - <c>JumpState</c>'s input buffer cost exactly that,
        /// and it was in this list the whole time, unread.
        ///
        /// Most entries are correct and want no action: a sprite, a settings
        /// object, the collision query. It goes to the log so it can be read
        /// against a symptom rather than guessed at.
        /// </summary>
        private void ReportUncoveredState()
        {
            PlayerContext context = MultiplayerRuntime.GetContext(LocalPlayer);
            if (context == null || !context.IsAlive)
            {
                return;
            }

            System.Collections.Generic.IList<string> notes =
                PlayerSnapshot.Capture(context, 0).DescribeUncovered();

            JumpKing.Program.crashLog.AddErrorMessage(
                "Local Multiplayer netplay: snapshot does not follow " +
                notes.Count + " references: " +
                string.Join(", ", new System.Collections.Generic.List<string>(notes)
                    .ToArray())
            );
        }

        /// <summary>
        /// Writes both players' positions against the frame number, on both
        /// machines.
        ///
        /// The point is that the two logs can be lined up. A checksum says the two
        /// games have drifted; this says which player, in which direction, from
        /// which frame - and whether the disagreement began at a jump, a landing,
        /// or somewhere with nothing happening at all. Guessing at that from a
        /// description of what it looked like is what has been costing rounds.
        ///
        /// Off unless the settings file asks, like every other probe: it writes a
        /// line per player per frame, which is only bearable while it is being
        /// read.
        /// </summary>
        private void TraceFrame()
        {
            if (!ModEntry.Diagnostics.Netplay)
            {
                return;
            }

            for (int number = 1; number <= MultiplayerRuntime.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context == null || context.Body == null)
                {
                    continue;
                }

                byte input;
                bool known = number == LocalPlayer
                    ? _localInputs.TryGet(_frame, out input)
                    : _usedRemote.TryGet(_frame, out input);

                JumpKing.Program.crashLog.AddErrorMessage(
                    "netplay trace: f=" + _frame +
                    " p=" + number +
                    (number == LocalPlayer ? " local" : " remote") +
                    " x=" + context.Body.Position.X.ToString("F3") +
                    " y=" + context.Body.Position.Y.ToString("F3") +
                    " vx=" + context.Body.Velocity.X.ToString("F3") +
                    " vy=" + context.Body.Velocity.Y.ToString("F3") +
                    " in=" + (known ? input.ToString() : "?")
                );
            }
        }

        /// <summary>
        /// The host declares where and *when* the session begins.
        ///
        /// The frame number matters as much as the position. Without it the two
        /// machines agreed on where the players stood and not on which frame that
        /// was, so the same frame number meant a different amount of elapsed
        /// simulation on each - the divergence measured at exactly one step of
        /// gravity, 0.257, on frame four, growing from there.
        ///
        /// So the origin is one fact, sent once and applied whole.
        /// </summary>
        private void SendStart()
        {
            PlayerContext me = MultiplayerRuntime.GetContext(LocalPlayer);
            if (me == null || me.Body == null)
            {
                return;
            }

            _frame = 0;
            _startFrame = 0;

            // Nobody is moved. Both machines have to agree where both players are,
            // and teleporting one onto the other was the cheap way to get that -
            // it also threw away wherever the guest had climbed to, for no reason
            // beyond the host not knowing it. Saying where you are costs the same
            // and leaves both runs alone.
            Vector2 at = me.Body.Position;
            int length = NetplayPacket.WriteStart(_sendBuffer, _frame, at.X, at.Y);
            _transport.Broadcast(_sendBuffer, length);
        }

        private void HandleStart(byte[] payload, int length)
        {
            long frame;
            float x;
            float y;
            if (!NetplayPacket.ReadStart(payload, length, out frame, out x, out y))
            {
                return;
            }

            // Wherever they said they are, that is where this machine puts them.
            // Both sides do this, so both end up holding the same pair of
            // positions without either player being moved.
            PlaceRemoteAt(new Vector2(x, y));

            if (_startFrame >= 0)
            {
                // Already running - this is the peer answering with its own
                // position, which is all that was wanted from it.
                return;
            }

            // Position and frame together, in one step. Applying them separately is
            // what let the two sides agree on where the players were while
            // disagreeing about when.
            _startFrame = frame;
            _frame = frame;
            _localInputs.Reset();
            _usedRemote.Reset();
            _lastAppliedRemote = frame - 1;
            _rollback.Clear();

            // _remoteInputs is deliberately kept. Packets that arrived before the
            // origin did carry the opening frames, and clearing them punched a hole
            // at the start of the timeline that nothing could ever fill.

            // Answered so the host learns where this player is, rather than
            // assuming and moving somebody.
            PlayerContext me = MultiplayerRuntime.GetContext(LocalPlayer);
            if (me != null && me.Body != null)
            {
                int reply = NetplayPacket.WriteStart(
                    _sendBuffer,
                    frame,
                    me.Body.Position.X,
                    me.Body.Position.Y
                );
                _transport.Broadcast(_sendBuffer, reply);
            }

            NetplayNotice.Show("started with the host at frame " + frame);
        }

        /// <summary>
        /// The frame the session began on, or -1 before it has. Also the guard
        /// against a second Start restarting a running session.
        /// </summary>
        private long _startFrame = -1;

        /// <summary>
        /// Puts every player at one agreed point, at rest.
        ///
        /// Both bodies, not just the remote one: the two machines are mirror images
        /// of each other, so they only agree if every body starts from the same
        /// state on both. Velocity is cleared for the same reason - a body carried
        /// into the session mid-fall would be falling on one screen and standing on
        /// the other.
        /// </summary>
        private void PlaceRemoteAt(Vector2 position)
        {
            PlayerContext context = MultiplayerRuntime.GetContext(RemotePlayer);
            if (context == null || context.Body == null)
            {
                return;
            }

            context.Body.Position = position;
            context.Body.Velocity = Vector2.Zero;
            context.CameraSeeded = false;
            _rollback.Clear();
        }

        /// <summary>
        /// A digest of what the simulation currently holds, compared with the
        /// peer's to catch the two drifting apart.
        ///
        /// Positions only. They are what everything else ends up expressed in, and
        /// a digest that covered more would report differences that do not matter
        /// while costing more to compute every time.
        /// </summary>
        private uint ComputeChecksum()
        {
            uint hash = 2166136261u;
            for (int number = 1; number <= MultiplayerRuntime.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context == null || context.Body == null)
                {
                    continue;
                }

                hash = Mix(hash, (int)context.Body.Position.X);
                hash = Mix(hash, (int)context.Body.Position.Y);
            }

            return hash;
        }

        private static uint Mix(uint hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                return hash * 16777619u;
            }
        }

        /// <summary>How often the two sides compare notes. Once a second.</summary>
        private const int ChecksumInterval = 60;

        private void SendChecksum()
        {
            if (_frame < 0 || _frame % ChecksumInterval != 0)
            {
                return;
            }

            int length = NetplayPacket.WriteChecksum(
                _sendBuffer,
                _frame,
                ComputeChecksum()
            );
            _transport.Broadcast(_sendBuffer, length);
        }

        private void HandleChecksum(byte[] payload, int length)
        {
            long frame;
            uint theirs;
            if (!NetplayPacket.ReadChecksum(payload, length, out frame, out theirs))
            {
                return;
            }

            uint mine;
            if (!_checksums.TryGetValue(frame, out mine))
            {
                // That frame is not one we kept. Nothing to compare, and nothing
                // worth saying.
                return;
            }

            _checksums.Remove(frame);
            if (mine == theirs)
            {
                _reportedDesync = false;
                return;
            }

            if (_reportedDesync)
            {
                // Said once. Repeating it every second would bury everything else.
                return;
            }

            _reportedDesync = true;
            NetplayNotice.Show(
                "the two games have drifted apart (frame " + frame + ")"
            );
        }

        private readonly System.Collections.Generic.Dictionary<long, uint> _checksums =
            new System.Collections.Generic.Dictionary<long, uint>();

        private bool _reportedDesync;

        /// <summary>What the remote player's input for a frame is now known to be.</summary>
        private byte ResolveRemoteInput(long frame)
        {
            byte input;
            return _remoteInputs.TryGet(frame, out input)
                ? input
                : _remoteInputs.Predict(frame);
        }

        /// <summary>
        /// The earliest frame in the range whose real input differs from what was
        /// fed to the simulation, or -1 when they all agree.
        /// </summary>
        private long FirstWrongFrame(long from, long through)
        {
            for (long frame = from; frame <= through; frame++)
            {
                byte actual;
                if (!_remoteInputs.TryGet(frame, out actual))
                {
                    continue;
                }

                byte used;
                if (!_usedRemote.TryGet(frame, out used))
                {
                    // Never simulated with this frame's input, so there is nothing
                    // it could have spoiled.
                    continue;
                }

                if (used != actual)
                {
                    return frame;
                }
            }

            return -1;
        }

        /// <summary>
        /// What the simulation was actually given for the remote player, frame by
        /// frame - a guess or the real thing. Kept because "was the guess wrong"
        /// cannot be answered from the real inputs alone.
        /// </summary>
        private readonly InputTimeline _usedRemote = new InputTimeline();

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
            var timer = System.Diagnostics.Stopwatch.StartNew();

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

            _snapshotCount++;
            _snapshotMilliseconds += timer.Elapsed.TotalMilliseconds;
        }

        private void OnRosterChanged()
        {
            if (_transport.Peers.Count == 0 && _phase == Phase.Playing)
            {
                NetplayNotice.Show(
                    _transport.IsLobbyOwner
                        ? "your guest left"
                        : "the host closed the lobby"
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

                case NetplayPacket.Kind.Start:
                    HandleStart(payload, length);
                    break;

                case NetplayPacket.Kind.Checksum:
                    HandleChecksum(payload, length);
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

                // The other king appears now, not when the lobby opened: there is
                // finally somebody driving it.
                SetSecondPlayerPresent(true);

                // The host says where the session begins, and both machines put
                // both bodies there. Each side placing them wherever it happened to
                // be leaves the two simulations starting from different states,
                // which no amount of rolling back can close - rollback replays from
                // a snapshot, and a snapshot that was wrong to begin with stays
                // wrong. Joining somebody's game means arriving where they are.
                if (_transport.IsLobbyOwner)
                {
                    SendStart();
                }

                ReportUncoveredState();

                NetplayNotice.Show(
                    _transport.IsLobbyOwner
                        ? "connected - " + (_transport.PeerName ?? "a friend") +
                            " joined your lobby"
                        : "connected - playing with " +
                            (_transport.PeerName ?? "the host")
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

            // Recorded even before the origin is known. Frame numbers on the wire
            // are the host's and absolute, so an early packet is not ambiguous -
            // and dropping them was leaving a hole at the very start of the
            // timeline. Confirmation is measured by contiguity from the beginning,
            // so a hole there can never be filled: the confirmed frame stays at -1
            // for the rest of the session, and a rollback that only runs when the
            // confirmed frame advances then never runs at all.
            // The newest frame in the packet is the frame the sender was on. That
            // is the only thing telling this machine whether it is ahead, and
            // therefore whether it should wait.
            if (lastFrame > _peerFrame)
            {
                _peerFrame = lastFrame;
            }

            for (int i = 0; i < count; i++)
            {
                _remoteInputs.Record(lastFrame - count + 1 + i, payload[offset + i]);
            }
        }

        /// <summary>
        /// The peer's newest frame, from the newest input packet.
        ///
        /// Kept because a gap that grows can only be closed by the machine in front
        /// waiting, and this is the only thing that says which machine that is. The
        /// waiting itself is not implemented: doing it here stopped the counting
        /// and not the game, which left the local player replaying their last input
        /// - see BeforeGameUpdate.
        /// </summary>
        private long _peerFrame = -1;

        private int _stallReport;
        private int _consecutiveStalls;

        /// <summary>
        /// True while this machine's own frame should not advance and the world
        /// should not be simulated.
        /// </summary>
        public bool IsStalling
        {
            get { return _stallThisFrame; }
        }

        private bool _stallThisFrame;

        /// <summary>
        /// Whether this machine is far enough in front of the peer to wait a frame.
        /// </summary>
        private bool ShouldWaitForPeer()
        {
            if (_peerFrame < 0 || _frame - _peerFrame <= MaxFrameAdvantage)
            {
                _consecutiveStalls = 0;
                return false;
            }

            // A peer that has genuinely stopped - alt-tabbed, hitched, gone - must
            // not freeze this game indefinitely. Past this the gap is accepted and
            // the guessing resumes; whether to leave is the player's call, not
            // something to decide by stalling for ever.
            if (_consecutiveStalls >= MaxStallFrames)
            {
                return false;
            }

            _consecutiveStalls++;
            return true;
        }

        /// <summary>
        /// How far in front of the peer this machine may get before it waits.
        ///
        /// Not zero: the two are never exactly level, and stopping on every frame
        /// of ordinary jitter would cost more than the gap does. Wide enough to
        /// absorb that, narrow enough that the guessing stays short.
        /// </summary>
        private const int MaxFrameAdvantage = 6;

        /// <summary>Half a second. Past it the peer is treated as stopped.</summary>
        private const int MaxStallFrames = 30;

        /// <summary>
        /// Refuses the session, saying why. The reason is the whole point: a
        /// mismatched build or level is something the player can act on, and
        /// "refused" on its own is not.
        /// </summary>
        private void Refuse(string reason)
        {
            _phase = Phase.Refused;
            NetplayNotice.Show("refused - " + reason);
            _transport.LeaveLobby();
        }
    }
}
