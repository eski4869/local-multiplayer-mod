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
            Playing

            // There was a Refused phase here. Nothing moved out of it, and Host and
            // Join both require Idle, so being refused once ended netplay for the
            // rest of the run - on the very fault whose remedy is to update and try
            // again. Refusing is an outcome, not a place to stay: it now reports
            // the reason and leaves, which returns to Idle like any other ending.
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
        private const int InputDelayFrames = RollbackPlan.InputDelayFrames;

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

        /// <summary>
        /// What the remote player did, guessed or real, and the record of which.
        /// Shared with the offline harness, so the game runs the same code the tests
        /// do rather than a second implementation written to match it.
        /// </summary>
        private readonly RemoteInputResolver _resolver;

        public NetplaySession()
        {
            _resolver = new RemoteInputResolver(_remoteInputs, _usedRemote);
            _correctionWorld = new CorrectionWorld(this);
        }

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
        /// Imposed for the session's lifetime rather than written to the player's
        /// settings. What they chose for this machine is theirs, and a session
        /// borrowing that field to say "two people are playing right now" is what
        /// left the game in offline two-player after a session ended by closing the
        /// window - there is no teardown to undo it on that path, so there must be
        /// nothing to undo.
        /// </summary>
        private void SetSecondPlayerPresent(bool present)
        {
            ModEntry.SetNetplayPlayerMode(
                present ? 2 : 0,
                TwoPlayerLayout.Shared
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
                    NetplayPacket.ProtocolVersion + ")",
                    NetplayPacket.RefusalReason.Protocol
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
            if (_phase != Phase.Idle && !_refusing)
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
            _localFrameAdvantage.Reset();
            _remoteFrameAdvantage.Reset();

            // Left counting, this would have the next session declare its peer gone
            // before they had a chance to say anything.
            _framesSincePeerSpoke = 0;
            _peerHasSpoken = false;
            _framesHandshaking = 0;

            // Left where the ended session put it, the next one would report
            // nothing until it had run as many frames as this one did.
            _lastReportedFrame = -1;

            // A repair owed to a session that has ended is not owed to the next one.
            _beyondRepair = false;
            _framesSinceSync = SyncInterval;

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
        /// <summary>
        /// How long this machine really took over its last frame, and whether the
        /// game had to catch up.
        /// </summary>
        /// <remarks>
        /// Measured wall-clock, because nothing else here can see it. The fixed
        /// timestep makes every frame report a sixtieth of a second whatever
        /// actually happened, and the mod's own timers only ever say what the mod
        /// itself cost. Between them they cannot tell a machine that is too slow
        /// for this game from a machine this mod is slowing down.
        /// </remarks>
        public void NoteFrameTiming(bool runningSlowly)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastFrameTimestamp > 0)
            {
                _frameMilliseconds +=
                    (now - _lastFrameTimestamp) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
                _frameTimings++;
            }

            _lastFrameTimestamp = now;

            if (runningSlowly)
            {
                _slowFrames++;
            }
        }

        /// <summary>
        /// A total over the reporting second, printed as what one frame cost.
        /// The frame budget is what everything here has to be judged against, so
        /// nothing is reported in units that have to be divided first.
        /// </summary>
        private string PerFrame(double total)
        {
            return _frameTimings == 0
                ? "0.00"
                : (total / _frameTimings).ToString("F2");
        }

        /// <summary>The frame the cost report last covered.</summary>
        private long _lastReportedFrame = -1;

        private long _lastFrameTimestamp;
        private double _frameMilliseconds;
        private int _frameTimings;
        private int _slowFrames;

        public void BeforeGameUpdate(float delta)
        {
            // Cleared before anything can return early. Every path out of here
            // still reaches EntityManager.Update afterwards, so a flag left set
            // from an earlier frame would hold the world still on a frame that was
            // never asked to wait - including after the session has ended.
            _stallThisFrame = false;

            if (_phase == Phase.Idle)
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
                // Left without a clock deliberately. This is a person being asked
                // to load a level, and the lobby membership is what holds the place
                // open while they do it - which is the whole reason this phase
                // exists rather than the join simply failing.
                //
                // It is bounded from the other side instead: the host's handshake
                // clock runs out, and the lobby closing takes this with it.
                return;
            }

            if (_phase == Phase.WaitingForPeer)
            {
                // No clock on this one on purpose: a lobby with nobody in it is
                // waiting for an invitation to be accepted, which is a person's
                // decision and takes as long as it takes.
                _framesHandshaking = 0;
                SendHello();
                return;
            }

            if (_phase == Phase.Handshaking)
            {
                // Bounded, because a handshake that is not completing is not going
                // to complete. Somebody is in the lobby and the two are exchanging
                // greetings that go nowhere - a build that answers differently, a
                // peer whose game closed between joining and replying. Without a
                // clock this sits here for ever, sending a greeting sixty times a
                // second to somebody who is not going to answer, with the menu
                // still claiming a session is being set up.
                //
                // GGPO gives its synchronising state a timeout for the same reason.
                // Every state a session can be in needs a way out of it; that was
                // the fault behind a refusal ending netplay for the rest of the run,
                // and this is the same fault one state along.
                if (++_framesHandshaking > HandshakeLimit)
                {
                    NetplayNotice.Show(
                        "could not agree a session with " + PeerNameOrDefault
                    );
                    Leave();
                    return;
                }

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
            // Sampled on every real frame, and before the decision that reads it.
            //
            // This was taken after the frame advanced, which meant a stalled frame
            // took no sample - so the average that decides whether to stall could
            // only be updated by not stalling. It deadlocked exactly as that
            // describes: twenty-nine consecutive reports with adv_l frozen at -4.1
            // while raw moved from -2 to -8, the peer eight frames in front and
            // this machine still holding itself back to let them catch up. The game
            // stopped.
            //
            // A measurement must never be gated on the thing it is measuring.
            _localFrameAdvantage.Add(LocalFrameAdvantage);

            // A peer that has stopped speaking has stopped playing.
            //
            // Nothing noticed before. A session whose other side had quit carried
            // on predicting them for ever - a log ends with the guessed-ahead
            // distance climbing past two hundred and eighty frames, four and a half
            // seconds of a king being puppeted by a guess, snapshots still being
            // taken for corrections that could never arrive. Whatever that looks
            // like on screen, it is not the other player.
            // Only once they have spoken at all.
            //
            // Silence means "was playing, stopped". Applied to a peer who has not
            // started yet it means something else entirely, and it closed a session
            // two seconds after it opened: the guest had not sent its first input,
            // the host called that a disconnection, and the session ended before it
            // began. A peer still getting to the level is not a peer who has left.
            //
            // What made that state reachable at all was the origin packet going out
            // unreliably - one datagram, sent once, and a guest that never got it
            // never advances and never speaks. That is fixed at the transport now,
            // which is where it belonged; this stays because a timeout should
            // measure what its name says regardless.
            if (_peerHasSpoken && ++_framesSincePeerSpoke > PeerSilenceLimit)
            {
                NetplayNotice.Show(PeerNameOrDefault + " disconnected");
                Leave();
                return;
            }

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
        /// Simulates an extra frame, without drawing one, when this machine has
        /// fallen behind.
        /// </summary>
        /// <remarks>
        /// The other half of holding back the machine in front. Stalling alone lets
        /// the pair keep pace but never closes a gap already opened; this closes it,
        /// and the two together mean a machine that drops behind returns rather
        /// than staying a fixed distance back for the rest of the session.
        ///
        /// **It is affordable because it skips the drawing.** This runs from the
        /// update postfix, so the frame it adds costs a simulation and nothing else,
        /// while the game draws once as usual - two frames of world for one frame of
        /// pixels. Drawing is the larger half, so a machine too slow to run the game
        /// at full rate can still often afford this, which is the whole reason it
        /// works on the machine that needs it.
        ///
        /// **Only over input that has already arrived.** Racing ahead on guesses
        /// would buy frames that a correction has to pay for again, and the machine
        /// doing this is the one that can least afford to pay twice. Stopping at the
        /// confirmed frontier also makes it self-limiting: it runs while there is
        /// known ground to cover and stops on its own when there is not.
        ///
        /// One frame per real frame. Two at a time would recover faster and give
        /// the slower machine three simulations in a budget it is already missing,
        /// which is how catching up turns into falling further behind.
        /// </remarks>
        private void CatchUpIfBehind()
        {
            if (Resimulation.IsActive || IsGamePaused || _startFrame < 0)
            {
                return;
            }

            long confirmed = _remoteInputs.ConfirmedThrough;
            if (confirmed - _frame <= CatchUpThreshold)
            {
                return;
            }

            EntityManager manager = EntityManager.instance;
            if (manager == null)
            {
                return;
            }

            long began = FrameCost.Now;

            _frame++;
            _localFrameAdvantage.Add(LocalFrameAdvantage);
            _clock.NoteSimulatedFrame();
            CaptureLocalInput();
            SendInputs();
            RollBackIfMispredicted();

            manager.Update(_frameDelta);

            if (_remoteInputs.ConfirmedThrough < _frame)
            {
                StoreSnapshots(_frame);
            }

            _catchUpFrames++;
            FrameCost.AddCatchUp(began);
        }

        private int _catchUpFrames;

        /// <summary>
        /// How far behind the confirmed input this machine must be before it starts
        /// covering ground twice as fast.
        ///
        /// Not one or two frames: the input delay means the simulation is meant to
        /// run a little behind what has arrived, and treating that as lateness would
        /// have every machine permanently catching up on nothing.
        /// </summary>
        private const int CatchUpThreshold = 4;

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

            // Only frames a correction could return to.
            //
            // A rollback restores to the first frame whose guess was wrong, and a
            // frame simulated from input that had already arrived was not a guess -
            // nothing can ever be found wrong about it, so nothing will ever ask
            // for its snapshot. Taking one anyway was the single most expensive
            // thing this mod did per frame, and on the stretches where the peer's
            // input was arriving ahead of the simulation it bought precisely
            // nothing.
            //
            // The test is whether anything is outstanding at all rather than
            // whether this particular frame was a guess, because a correction
            // restores one frame and replays from there - so the frame before the
            // wrong guess is wanted too, and it is cheaper to keep a frame that
            // turns out unwanted than to be missing one.
            if (_remoteInputs.ConfirmedThrough < _frame)
            {
                StoreSnapshots(_frame);
            }

            SendSyncIfBeyondRepair();
            CatchUpIfBehind();

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

            bool predicted;
            input = _resolver.Resolve(at, out predicted);
            if (predicted)
            {
                _predictedFrames++;
            }
            else
            {
                _realFrames++;
            }

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
            _transport.BroadcastReliable(_sendBuffer, length);
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
                LocalFrameAdvantage,
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
            _transport.BroadcastReliable(_sendBuffer, length);
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
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Correction.Result result = Correction.Run(_correctionWorld);

            if (result.Outcome == Correction.Outcome.Applied)
            {
                _rollbackCount++;
                _resimulatedFrames += result.Replayed;
                _resimulateMilliseconds += timer.Elapsed.TotalMilliseconds;
                return;
            }

            // A correction that was needed and could not be made. Recomputing has
            // run out of road: there is no saved frame to return to, or returning
            // would cost more than a frame has. Every later correction inherits the
            // divergence this one could not repair, so nothing downstream fixes it.
            //
            // This used to end here, with a notice and a drift. It is now the
            // moment the host declares where everyone is instead - which is the
            // trade this whole mechanism exists to make, taken at the one point
            // where the cheaper option has genuinely been exhausted.
            if (result.Outcome == Correction.Outcome.TooLate ||
                result.Outcome == Correction.Outcome.TooExpensive ||
                result.Outcome == Correction.Outcome.RestoreFailed)
            {
                _beyondRepair = true;
            }
        }

        /// <summary>
        /// The session, seen as the thing a correction acts on.
        ///
        /// Kept as a separate object rather than implementing the interface on the
        /// session itself, so the surface a correction is allowed to touch is
        /// visible and small: a few frame numbers, restore, replay, report.
        /// </summary>
        private sealed class CorrectionWorld : ICorrectionWorld
        {
            private readonly NetplaySession _session;

            public CorrectionWorld(NetplaySession session)
            {
                _session = session;
            }

            public long CurrentFrame
            {
                get { return _session._frame; }
            }

            public long RemoteConfirmedThrough
            {
                get { return _session._remoteInputs.ConfirmedThrough; }
            }

            public long LastAppliedRemote
            {
                get { return _session._lastAppliedRemote; }
                set { _session._lastAppliedRemote = value; }
            }

            public long FirstWrongInputFrame(long from, long through)
            {
                return _session._resolver.FirstWrongInputFrame(from, through);
            }

            public bool CanRestore(long simulationFrame)
            {
                return _session._rollback.CanRewindTo(simulationFrame) &&
                    simulationFrame < _session._frame;
            }

            public bool Restore(long simulationFrame)
            {
                return _session.RestoreAll(simulationFrame);
            }

            public void ReplayFrame(long simulationFrame)
            {
                EntityManager manager = EntityManager.instance;
                if (manager == null)
                {
                    return;
                }

                // The frame number is moved so the players read the input that frame
                // consumes, and moved back so the session's own place is not lost.
                long resumed = _session._frame;
                _session._frame = simulationFrame;

                using (Resimulation.Enter())
                {
                    manager.Update(_session._frameDelta);
                }

                _session.StoreSnapshots(simulationFrame);
                _session._frame = resumed;
            }

            public void Report(string message)
            {
                NetplayNotice.Show(message);
            }
        }

        private readonly CorrectionWorld _correctionWorld;

        /// <summary>
        /// How far back a correction may reach. Every frame in the range is the
        /// whole world simulated again inside one real frame, so this is a cost
        /// ceiling before it is anything else.
        /// </summary>
        private const int MaxRollbackFrames = RollbackPlan.MaxRollbackFrames;

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
            // Measured in frames simulated, not in arriving at a multiple of them.
            //
            // The test used to be `_frame % 60 == 0`, and the frame does not advance
            // while the machine is waiting - so a machine that stopped on a multiple
            // of sixty wrote this line on every real frame instead of every sixtieth.
            // Earlier logs carry a hundred and six copies of the report for frame
            // 360 and twenty-nine for frame 420. The crash log is unbuffered, so
            // each one is a write to disk, sixty times a second, beginning exactly
            // when the machine is already failing to finish its frames. The
            // measurement was feeding the thing it was measuring.
            //
            // Counting frames since the last report is right at both ends: a stall
            // cannot repeat one, and a catch-up frame stepping over a multiple of
            // sixty cannot skip one.
            if (_frame < 0 ||
                _frame - _lastReportedFrame < NetplayClock.FramesPerSecond)
            {
                return;
            }

            _lastReportedFrame = _frame;

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
                // raw is the frame numbers straight off the wire, which is the gap
                // and the travel time together and was mistaken for the gap alone.
                // gap is the two sides' measurements differenced, which is the gap.
                " raw=" + (_peerFrame < 0 ? 0 : _frame - _peerFrame) +
                " adv_l=" + _localFrameAdvantage.Average.ToString("F1") +
                " adv_r=" + _remoteFrameAdvantage.Average.ToString("F1") +
                " gap=" + FramesToWaitOut +
                " stalled=" + _stallReport +

                // Only one thing stops the game now: the prediction window filling.
                // gap above says whether the machines are keeping pace, which is
                // what the removed frame-advantage stall used to act on.
                " stall_pred=" + _predictionStalls +

                // Whether the misprediction search is finding nothing wrong or
                // looking at nothing. Both report zero rollbacks.
                // What this machine's frames really cost, against the 16.7ms it has.
                // slow is MonoGame reporting it could not finish in time and had to
                // catch up - which is the only thing here that can say the hardware
                // is the problem rather than this mod.
                " frame_ms=" + (_frameTimings == 0
                    ? "0.0"
                    : (_frameMilliseconds / _frameTimings).ToString("F2")) +
                " slow=" + _slowFrames +

                // This mod's own share of that frame, broken out. Per frame, so it
                // subtracts directly from frame_ms and can be compared against the
                // 16.67ms the game has.
                " sim_ms=" + PerFrame(FrameCost.SimulationMilliseconds) +
                " p2_ms=" + PerFrame(FrameCost.AdditionalPlayerMilliseconds) +
                " scope_ms=" + PerFrame(FrameCost.ScopeMilliseconds) +
                " draw_ms=" + PerFrame(FrameCost.DrawMilliseconds) +
                " catchup=" + _catchUpFrames +
                " sync_out=" + _syncsSent +
                " sync_in=" + _syncsApplied +
                " catchup_ms=" + PerFrame(FrameCost.CatchUpMilliseconds) +

                " compared=" + _resolver.Compared +
                " skip_noactual=" + _resolver.SkippedNoActual +
                " skip_noused=" + _resolver.SkippedNoUsed
            );

            _resolver.ResetCounters();
            _catchUpFrames = 0;
            _syncsSent = 0;
            _syncsApplied = 0;
            FrameCost.Reset();
            _frameMilliseconds = 0;
            _frameTimings = 0;
            _slowFrames = 0;
            _predictionStalls = 0;
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
            _transport.BroadcastReliable(_sendBuffer, length);
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
                _transport.BroadcastReliable(_sendBuffer, reply);
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
        /// What the simulation was actually given for the remote player, frame by
        /// frame - a guess or the real thing. Kept because "was the guess wrong"
        /// cannot be answered from the real inputs alone.
        /// </summary>
        private readonly InputTimeline _usedRemote = new InputTimeline();

        private bool RestoreAll(long frame)
        {
            // Gathered before any of it is applied. Restoring as it went meant a
            // player missing a snapshot left the ones before it already rewound and
            // the ones after it in the present - half a world in the past, and the
            // caller told only that it failed.
            int count = MultiplayerRuntime.PlayerCount;
            var contexts = new PlayerContext[count + 1];
            var snapshots = new PlayerSnapshot[count + 1];

            for (int number = 1; number <= count; number++)
            {
                contexts[number] = MultiplayerRuntime.GetContext(number);
                if (contexts[number] == null ||
                    !_rollback.TryGet(number, frame, out snapshots[number]))
                {
                    return false;
                }
            }

            for (int number = 1; number <= count; number++)
            {
                if (!snapshots[number].Restore(contexts[number]))
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
            // From any state a session can be in, not only from play.
            //
            // This asked for Playing, so a peer who left during the handshake was
            // not noticed at all: the session stayed in Handshaking over an empty
            // lobby, and ten seconds later the handshake clock reported "could not
            // agree a session" - which is a different thing from "they left", and
            // sends the player looking for a disagreement that never happened.
            //
            // That is exactly how the last failure presented. A guest on an older
            // build read the lobby, refused, and left; the host saw none of it and
            // blamed the handshake.
            if (_transport.Peers.Count == 0 && _phase != Phase.Idle)
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

                case NetplayPacket.Kind.Refused:
                    HandleRefused(payload, length);
                    break;

                case NetplayPacket.Kind.Sync:
                    HandleSync(payload, length);
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
                    NetplayPacket.ProtocolVersion + ")",
                    NetplayPacket.RefusalReason.Protocol
                );
                return;
            }

            ulong mine = NetplayLevelIdentity.Current;
            if (mine == 0 || levelHash != mine)
            {
                // Named rather than left to show up as a peer standing on nothing.
                // A workshop map updates in place under one id, so "the same map"
                // is not the same claim as the same block layout.
                Refuse(
                    "the two of you are on different levels, or different versions of one",
                    NetplayPacket.RefusalReason.Level
                );
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

        /// <summary>
        /// Four numbers per player - where they are and where they are going.
        /// </summary>
        private const int SyncValuesPerPlayer = 4;

        private readonly float[] _syncValues =
            new float[SyncValuesPerPlayer * MultiplayerRuntime.MaximumPlayers];

        /// <summary>
        /// The host putting the session back together by declaring where everyone
        /// is, when recomputing can no longer reach.
        /// </summary>
        /// <remarks>
        /// **Only the host sends this, and it decides alone.** Two machines that
        /// have stopped agreeing cannot be asked to agree on who is right - that is
        /// the same problem again, one level up. The host is the one fact Steam
        /// guarantees both read identically, so it is the one that says.
        ///
        /// Throttled, because the condition that triggers it persists for a moment
        /// after it is answered: the guest needs frames to receive, apply and be
        /// seen applying it, and a second declaration arriving mid-repair undoes
        /// the first.
        /// </remarks>
        private void SendSyncIfBeyondRepair()
        {
            if (!_transport.IsLobbyOwner || !IsPlaying || _startFrame < 0)
            {
                return;
            }

            if (_framesSinceSync < SyncInterval)
            {
                _framesSinceSync++;
                return;
            }

            if (!_beyondRepair)
            {
                return;
            }

            _beyondRepair = false;
            _framesSinceSync = 0;

            int count = 0;
            for (int number = 1; number <= MultiplayerRuntime.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context == null || !context.IsAlive || context.Body == null)
                {
                    return;
                }

                _syncValues[count++] = context.Body.Position.X;
                _syncValues[count++] = context.Body.Position.Y;
                _syncValues[count++] = context.Body.Velocity.X;
                _syncValues[count++] = context.Body.Velocity.Y;
            }

            int written = NetplayPacket.WriteSync(
                _sendBuffer, _frame, _syncValues, count
            );

            if (written > 0)
            {
                _transport.BroadcastReliable(_sendBuffer, written);
                _syncsSent++;
            }
        }

        /// <summary>
        /// The other side saying it will not play, and why.
        /// </summary>
        /// <remarks>
        /// Reported rather than acted on beyond ending the session. There is
        /// nothing to negotiate: the disagreement that caused it - different
        /// builds, different levels - is not one either machine can settle from
        /// here. What the player needs is to know which of the two it was, because
        /// that is the difference between updating the mod and picking a different
        /// map.
        /// </remarks>
        private void HandleRefused(byte[] payload, int length)
        {
            NetplayPacket.RefusalReason reason;
            if (!NetplayPacket.ReadRefused(payload, length, out reason))
            {
                return;
            }

            string said;
            switch (reason)
            {
                case NetplayPacket.RefusalReason.Protocol:
                    said = "they are running a different version of this mod";
                    break;

                case NetplayPacket.RefusalReason.Level:
                    said = "they are on a different level, or a different version of one";
                    break;

                default:
                    said = "they did not say why";
                    break;
            }

            NetplayNotice.Show(PeerNameOrDefault + " refused - " + said);

            _refusing = true;
            try
            {
                Leave();
            }
            finally
            {
                _refusing = false;
            }
        }

        private void HandleSync(byte[] payload, int length)
        {
            // The host does not take instruction on where things are; it is the one
            // giving it. A packet arriving the other way is either an older build
            // or a lobby whose ownership moved, and adopting it would have the two
            // machines pulling each other back and forth.
            if (_transport.IsLobbyOwner || !IsPlaying)
            {
                return;
            }

            long frame;
            int count;
            if (!NetplayPacket.ReadSync(
                payload, length, out frame, _syncValues, out count
            ))
            {
                return;
            }

            if (count < MultiplayerRuntime.PlayerCount * SyncValuesPerPlayer)
            {
                return;
            }

            int at = 0;
            for (int number = 1; number <= MultiplayerRuntime.PlayerCount; number++)
            {
                PlayerContext context = MultiplayerRuntime.GetContext(number);
                if (context == null || !context.IsAlive || context.Body == null)
                {
                    return;
                }

                context.Body.Position = new Vector2(
                    _syncValues[at], _syncValues[at + 1]
                );
                context.Body.Velocity = new Vector2(
                    _syncValues[at + 2], _syncValues[at + 3]
                );
                at += SyncValuesPerPlayer;

                // The camera followed a king who is no longer where it was looking.
                context.CameraSeeded = false;
            }

            // Adopting the host's clock as well as its positions. Landing on the
            // right spot at the wrong frame just reopens the gap that caused this.
            _frame = frame;

            // Everything kept for correcting describes a history this machine has
            // just stopped having. Left in place, the next correction would rewind
            // into it and undo the repair.
            _rollback.Clear();
            _usedRemote.Reset();
            _lastAppliedRemote = frame;

            NetplayNotice.Show("resynchronised with " + PeerNameOrDefault);
            _syncsApplied++;
        }

        /// <summary>Set when a correction was needed and could not be made.</summary>
        private bool _beyondRepair;

        private int _framesSinceSync;
        private int _syncsSent;
        private int _syncsApplied;

        /// <summary>
        /// Half a second between declarations, so one has time to be applied and
        /// observed before another is considered.
        /// </summary>
        private const int SyncInterval = 30;

        private void HandleInput(byte[] payload, int length)
        {
            long lastFrame;
            int frameAdvantage;
            int count;
            int offset;
            if (!NetplayPacket.ReadInput(
                payload,
                length,
                out lastFrame,
                out frameAdvantage,
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
            // The newest frame in the packet is the frame the sender was on when it
            // sent - so this machine's distance from it is the real gap plus the
            // travel time, and taken alone it cannot say which. The sender's own
            // measurement, which contains the same travel time, is what separates
            // them.
            if (lastFrame > _peerFrame)
            {
                _peerFrame = lastFrame;
            }

            _remoteFrameAdvantage.Add(frameAdvantage);
            _framesSincePeerSpoke = 0;
            _peerHasSpoken = true;

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

        /// <summary>Frames spent exchanging greetings that go nowhere.</summary>
        private int _framesHandshaking;

        /// <summary>
        /// Ten seconds. Generous, because the other side may be loading a level,
        /// and finite, because a handshake that has not completed by then is not
        /// going to.
        /// </summary>
        private const int HandshakeLimit = 600;

        /// <summary>Whether the peer has ever sent an input.</summary>
        private bool _peerHasSpoken;

        /// <summary>Frames since anything arrived from the peer.</summary>
        private int _framesSincePeerSpoke;

        /// <summary>
        /// Two seconds of silence, after which the peer is gone rather than slow.
        ///
        /// Long enough to outlast any hitch worth waiting through - a load, a
        /// collection, a lost burst of packets - and far short of the four and a
        /// half seconds a real session spent puppeting a player who had already
        /// quit.
        /// </summary>
        private const int PeerSilenceLimit = 120;

        private readonly FrameAdvantageWindow _localFrameAdvantage =
            new FrameAdvantageWindow();

        private readonly FrameAdvantageWindow _remoteFrameAdvantage =
            new FrameAdvantageWindow();

        /// <summary>
        /// A short rolling average of a frame-advantage measurement.
        ///
        /// Each sample carries whatever jitter its packet met on the way, and
        /// stalling the game is far too blunt a response to spend on one noisy
        /// reading. The window is long enough to see past a single late packet and
        /// short enough to notice a machine genuinely pulling away.
        /// </summary>
        private sealed class FrameAdvantageWindow
        {
            private const int Size = 30;

            private readonly int[] _samples = new int[Size];
            private int _count;
            private int _next;
            private long _sum;

            public bool HasSamples
            {
                get { return _count > 0; }
            }

            public float Average
            {
                get { return _count == 0 ? 0f : (float)_sum / _count; }
            }

            public void Add(int sample)
            {
                if (_count == Size)
                {
                    _sum -= _samples[_next];
                }
                else
                {
                    _count++;
                }

                _samples[_next] = sample;
                _sum += sample;
                _next = (_next + 1) % Size;
            }

            public void Reset()
            {
                _count = 0;
                _next = 0;
                _sum = 0;
            }
        }

        private int _stallReport;
        private int _consecutiveStalls;
        private int _predictionStalls;

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
        /// Whether this machine must wait a frame rather than advance.
        ///
        /// Two separate reasons, and confusing them is what this cost rounds of
        /// testing to learn. They measure different things:
        ///
        /// The prediction window is how far ahead of *confirmed input* this machine
        /// is - how much it is currently guessing. It bounds what a correction can
        /// cost, because a correction re-simulates every frame back to the wrong
        /// guess, all inside one real frame. Let the window grow and each
        /// correction gets more expensive, which slows the machine, which widens
        /// the window: that is the "gets worse and worse once it starts" spiral,
        /// and it is not a symptom to chase but the absence of this bound.
        ///
        /// Frame advantage is how far ahead of the *peer's clock* this machine is -
        /// how much sooner it arrives at the same frame. That is what a difference
        /// in hardware produces, and only the machine in front waiting closes it.
        ///
        /// A connection can fill the prediction window while the clocks are level,
        /// and a fast machine can run away while every input arrives on time. Both
        /// have to be checked.
        /// </summary>
        private bool ShouldWaitForPeer()
        {
            long confirmed = _remoteInputs.ConfirmedThrough;

            bool guessingTooFar =
                confirmed >= 0 && _frame - confirmed >= MaxPredictionFrames;

            // The frame-advantage stall is measured and reported but no longer
            // acted on. See FramesToWaitOut for why.
            if (!guessingTooFar)
            {
                _consecutiveStalls = 0;
                return false;
            }

            _predictionStalls++;

            _consecutiveStalls++;

            // Long enough that the player knows the freeze is the other machine and
            // not a crash. A silent stop reads as one.
            if (_consecutiveStalls == NoticeAfterStalls)
            {
                NetplayNotice.Show("waiting for " + PeerNameOrDefault);
            }

            // **This wait is not given up on.**
            //
            // It was, after nine frames, so that a peer who had crashed could not
            // freeze the game for ever. That reasoning was right and the remedy was
            // in the wrong place: giving up turns the prediction window from a
            // bound into a preference, and a machine that is merely faster than its
            // peer then runs away without limit. The host's log shows exactly that
            // - stall_pred=60 against stalled=0, every frame deciding to wait and
            // every frame proceeding anyway - until it was a hundred and ninety
            // seven frames, better than three seconds, ahead. For all of it the
            // other king was being driven entirely by guesswork. That is not
            // desynchronisation; there was simply nobody there.
            //
            // A peer who has actually gone is handled by silence instead, two
            // seconds of it, which ends the session and says who left. That
            // separates "gone" from "slow" by asking the right question: a slow
            // peer keeps sending, so it keeps this wait honest, and waiting is
            // exactly what should happen - both machines then run at the speed of
            // the slower one, which is the trade rollback makes.
            return true;
        }

        private string PeerNameOrDefault
        {
            get
            {
                string name = PeerName;
                return string.IsNullOrEmpty(name) ? "the other player" : name;
            }
        }

        /// <summary>
        /// How far ahead of confirmed input this machine may guess before it waits.
        ///
        /// This is the ceiling on what one correction can cost: no wrong guess can
        /// be older than this, so no correction re-simulates more than this many
        /// frames. Eight is the number rollback implementations have settled on -
        /// about 130ms at sixty frames a second, which covers ordinary connections
        /// while keeping the worst correction to eight replayed frames.
        /// </summary>
        private const int MaxPredictionFrames = RollbackPlan.MaxPredictionFrames;

        /// <summary>
        /// How far behind the peer this machine measures itself to be, negative
        /// when it is the one ahead. Sent to the peer every packet.
        /// </summary>
        /// <remarks>
        /// This number on its own is not the gap. The peer's frame is where it was
        /// when it sent, so this is the gap plus the travel time, and no amount of
        /// care on one machine can separate those. What makes it usable is that the
        /// peer computes the same quantity about this machine, and the same travel
        /// time is inside both - so the difference cancels it. See
        /// <see cref="FramesToWaitOut"/>.
        /// </remarks>
        private int LocalFrameAdvantage
        {
            get
            {
                return _peerFrame < 0 ? 0 : (int)(_peerFrame - _frame);
            }
        }

        /// <summary>
        /// How many frames this machine would wait out to let the peer catch up.
        /// Measured and reported; **not acted on**.
        /// </summary>
        /// <remarks>
        /// **Two different things stop the game, and only one of them is a
        /// requirement.**
        ///
        /// The prediction window is the requirement. It caps what a correction can
        /// cost, because a correction replays every frame back to the wrong guess
        /// inside one real frame; without it each correction gets more expensive,
        /// which slows the machine, which widens the window. Removing that would
        /// bring back the spiral where one bad moment never recovers.
        ///
        /// This one is not. Holding back the machine in front only makes the
        /// prediction window fill less often - it spreads a cost that the window
        /// already bounds. Standard implementations do it, and it is worth having
        /// when it works.
        ///
        /// It did not work here, twice, and both times it stopped the game
        /// outright: first by mistaking the travel time for a gap and stalling
        /// permanently on a healthy connection, then by freezing the measurement it
        /// needed to stop stalling. Meanwhile the thing it protects - your own
        /// input reaching your own king without waiting for the network - is what
        /// it damages when it is wrong, because it stops the whole world including
        /// you.
        ///
        /// So the measurement stays, in the cost report as `gap`, where it says
        /// whether the two machines are keeping pace. Acting on it waits until it
        /// can be proven against an offline harness rather than against a person
        /// starting a game.
        /// </remarks>
        /// <remarks>
        /// **This replaced comparing frame numbers directly, which measured the
        /// wrong thing entirely.** The peer's frame number arrives late by exactly
        /// the travel time, so the raw difference never falls below the latency,
        /// however perfectly the two machines are keeping pace. Against a fixed
        /// threshold that reads as permanent frame advantage: the game stalled
        /// around ten frames a second on a connection that was doing nothing wrong,
        /// and the stalling *was* the lag being complained about.
        ///
        /// Averaging both sides' measurements and halving the difference is what
        /// rollback implementations do, and it is not a heuristic - the travel time
        /// appears once in each measurement with the same sign, so subtracting
        /// removes it exactly, leaving twice the true gap.
        ///
        /// Averaged over a window because a single sample carries whatever jitter
        /// that one packet met, and waiting is far too blunt an instrument to spend
        /// on noise.
        /// </remarks>
        private int FramesToWaitOut
        {
            get
            {
                if (_peerFrame < 0 || !_remoteFrameAdvantage.HasSamples)
                {
                    return 0;
                }

                return RollbackPlan.FramesToWait(
                    _localFrameAdvantage.Average,
                    _remoteFrameAdvantage.Average
                );
            }
        }

        /// <summary>
        /// The gap worth stopping the game over.
        ///
        /// A frame or two apart is the normal condition of two machines and costs
        /// nothing - the prediction covers it. Stalling for that would trade a gap
        /// nobody can feel for a stutter everybody can.
        /// </summary>
        private const int MinWaitFrames = RollbackPlan.MinWaitFrames;

        /// <summary>Explained once the pause is long enough to be noticed.</summary>
        private const int NoticeAfterStalls = 6;

        /// <summary>
        /// The most frames in a row the game may be held still - about 150ms.
        ///
        /// Two seconds was allowed here, on the reasoning that a longer wait rides
        /// out a hitch that would otherwise cost synchronisation. That reasoning
        /// values the wrong thing. Two seconds of a frozen game is not a hitch
        /// ridden out, it is the failure - worse than the desynchronisation it was
        /// avoiding, and indistinguishable from a crash to the person holding the
        /// controller. When the wait runs out the gap is simply accepted and the
        /// guessing resumes.
        ///
        /// Nine is what rollback implementations cap a wait at, and it is a cap on
        /// the response, not on how long a peer may be slow: the gap is remeasured
        /// continuously and waiting resumes if it is still there.
        /// </summary>
        private const int MaxStallFrames = RollbackPlan.MaxStallFrames;

        /// <summary>
        /// Refuses the session, saying why. The reason is the whole point: a
        /// mismatched build or level is something the player can act on, and
        /// "refused" on its own is not.
        /// </summary>
        private void Refuse(string reason)
        {
            Refuse(reason, NetplayPacket.RefusalReason.Unknown);
        }

        /// <summary>
        /// Refusing a session, and telling the other side so.
        /// </summary>
        /// <remarks>
        /// The telling used to be missing. The refusing side reported the reason to
        /// its own player and left, and the other side saw somebody arrive and
        /// depart with no explanation - which works when the two people are in the
        /// same room and can say it out loud, and is not a design.
        ///
        /// Sent before leaving, reliably, because there is exactly one chance to
        /// send it.
        /// </remarks>
        private void Refuse(string reason, NetplayPacket.RefusalReason code)
        {
            if (code != NetplayPacket.RefusalReason.Unknown)
            {
                int length = NetplayPacket.WriteRefused(_sendBuffer, code);
                if (length > 0)
                {
                    _transport.BroadcastReliable(_sendBuffer, length);
                }
            }

            NetplayNotice.Show("refused - " + reason);

            // **Refusing a session is not a state to stay in.**
            //
            // This parked the phase in Refused and left the lobby, and nothing ever
            // moved it back: Host and Join both require Idle, so a player refused
            // once could not start or join anything for the rest of the run. The
            // most likely reason to be refused is a version mismatch, which is
            // exactly the case where the fix is to update and try again - and
            // trying again was the one thing that had been made impossible.
            //
            // Leaving properly reports what happened, takes the second body away
            // and returns to Idle, which is what refusing a session should mean.
            _refusing = true;
            try
            {
                Leave();
            }
            finally
            {
                _refusing = false;
            }
        }

        /// <summary>
        /// Set while leaving because a session was refused, so the leaving does not
        /// announce itself over the reason that was just given.
        /// </summary>
        private bool _refusing;
    }
}
