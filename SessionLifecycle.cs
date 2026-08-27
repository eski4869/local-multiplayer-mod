namespace LocalMultiplayerMod
{
    /// <summary>
    /// What a session does to the world as it starts, runs and ends.
    /// </summary>
    /// <remarks>
    /// Only effects. No Steam, no Jump King, no Harmony - so the rules that decide
    /// when these happen can be run in a test, which is where the last two failures
    /// would have been caught.
    /// </remarks>
    internal interface ISessionEffects
    {
        void SendHello();

        /// <summary>The host declaring which frame the session begins on.</summary>
        void SendStart();

        void LeaveLobby();

        void Notice(string message);

        /// <summary>Bring the second player in, or take them away.</summary>
        void SetPeerPresent(bool present);

        /// <summary>Forget everything kept about a session that has ended.</summary>
        void ResetSessionState();
    }

    /// <summary>
    /// Which state a session is in, and how it leaves each one.
    /// </summary>
    /// <remarks>
    /// **Every state needs a way out of it.** That sentence is the whole of this
    /// class, and both faults that stopped sessions starting were the same sentence
    /// unwritten:
    ///
    /// Refusing a session parked it in a state nothing moved out of, and starting
    /// or joining required being idle - so being refused once ended netplay for the
    /// rest of the run, on the one fault whose remedy is to update and try again.
    ///
    /// Handshaking had no clock, so a peer in the lobby who was never going to
    /// answer left the session greeting them sixty times a second while the menu
    /// said a session was being arranged.
    ///
    /// Neither was a hard problem. Both were invisible because the connection
    /// lifecycle had no test of any kind while the correction logic had a harness -
    /// the faults moved to where nobody was looking.
    /// </remarks>
    internal sealed class SessionLifecycle
    {
        public enum Phase
        {
            Idle,

            /// <summary>A lobby exists and nobody else is in it yet.</summary>
            WaitingForPeer,

            /// <summary>
            /// In the lobby, on a different level than the session is played on.
            ///
            /// Joining and connecting are not the same step. Accepting an
            /// invitation puts somebody in a lobby; it cannot put them on the right
            /// level, and changing level restarts their run. So they wait here, are
            /// told which level to load, and connect once they are on it - lobby
            /// membership outlasting the level change is what makes the wait usable
            /// rather than a dead end.
            /// </summary>
            NeedsLevel,

            /// <summary>Greetings being exchanged, on a clock.</summary>
            Handshaking,

            Playing
        }

        /// <summary>
        /// Ten seconds of greeting somebody who is not answering. Generous, because
        /// they may be loading, and finite, because a handshake that has not
        /// completed by then is not going to.
        /// </summary>
        public const int HandshakeLimitFrames = 600;

        private readonly ISessionEffects _effects;
        private Phase _phase = Phase.Idle;
        private int _framesHandshaking;

        public SessionLifecycle(ISessionEffects effects)
        {
            _effects = effects;
        }

        public Phase Current
        {
            get { return _phase; }
        }

        public bool IsPlaying
        {
            get { return _phase == Phase.Playing; }
        }

        /// <summary>Whether a new session may be started or joined.</summary>
        public bool CanBegin
        {
            get { return _phase == Phase.Idle; }
        }

        /// <summary>A lobby now exists, either created here or joined.</summary>
        public void LobbyEntered(bool created)
        {
            _framesHandshaking = 0;
            _phase = created ? Phase.WaitingForPeer : Phase.Handshaking;
        }

        /// <summary>This machine is not on the level the session is played on.</summary>
        public void NeedsDifferentLevel()
        {
            _phase = Phase.NeedsLevel;
        }

        /// <summary>This machine has reached the right level.</summary>
        public void LevelReady()
        {
            if (_phase == Phase.NeedsLevel)
            {
                _framesHandshaking = 0;
                _phase = Phase.Handshaking;
            }
        }

        /// <summary>Somebody arrived in the lobby.</summary>
        public void PeerArrived()
        {
            if (_phase == Phase.WaitingForPeer)
            {
                _framesHandshaking = 0;
                _phase = Phase.Handshaking;
            }
        }

        /// <summary>A greeting was understood and accepted.</summary>
        public void HelloAccepted(bool isLobbyOwner)
        {
            if (_phase != Phase.Handshaking)
            {
                return;
            }

            _phase = Phase.Playing;
            _effects.SetPeerPresent(true);

            if (isLobbyOwner)
            {
                _effects.SendStart();
            }
        }

        /// <summary>
        /// A greeting was understood and rejected - a different build, a different
        /// level.
        /// </summary>
        public void Refuse(string reason)
        {
            _effects.Notice("refused - " + reason);
            End(null);
        }

        /// <summary>Everyone else has gone.</summary>
        public void PeerLeft(bool isLobbyOwner)
        {
            if (_phase == Phase.Idle)
            {
                return;
            }

            End(isLobbyOwner ? "your guest left" : "the host closed the lobby");
        }

        /// <summary>The peer stopped sending after having started.</summary>
        public void PeerWentSilent(string peerName)
        {
            End(peerName + " disconnected");
        }

        /// <summary>This player ended it.</summary>
        public void Leave(bool isLobbyOwner)
        {
            if (_phase == Phase.Idle)
            {
                return;
            }

            End(isLobbyOwner ? "lobby closed" : "left the lobby");
        }

        /// <summary>
        /// One frame of waiting. Returns false when the session has ended itself.
        /// </summary>
        public bool Tick(string peerName)
        {
            if (_phase == Phase.WaitingForPeer)
            {
                // No clock here on purpose. An empty lobby is waiting for a person
                // to accept an invitation, which takes as long as it takes.
                _framesHandshaking = 0;
                _effects.SendHello();
                return true;
            }

            if (_phase != Phase.Handshaking)
            {
                return true;
            }

            if (++_framesHandshaking > HandshakeLimitFrames)
            {
                End("could not agree a session with " + peerName);
                return false;
            }

            _effects.SendHello();
            return true;
        }

        private void End(string reason)
        {
            if (reason != null)
            {
                _effects.Notice(reason);
            }

            _effects.LeaveLobby();
            _effects.SetPeerPresent(false);
            _effects.ResetSessionState();

            _phase = Phase.Idle;
            _framesHandshaking = 0;
        }
    }
}
