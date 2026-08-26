using System;
using System.Collections.Generic;
using Steamworks;

namespace LocalMultiplayerMod
{
    /// <summary>
    /// The Steam side: a lobby used as a roster, and packets sent peer to peer.
    ///
    /// The lobby is only a list of who is here. Nothing about the game travels
    /// through it, which is what makes joining, inviting and NAT traversal free -
    /// Steam already solves those, and solving them again would be the most
    /// expensive part of this by far.
    ///
    /// **Friends-only**, unlike the existing multiplayer mod, which defaults to a
    /// public lobby. A public lobby puts strangers on the roster, their P2P
    /// sessions are accepted automatically because they are on it, and what crosses
    /// is a live position - which is a streaming hazard, not a preference.
    ///
    /// Friends-only rather than private or invisible, because the lobby has to be
    /// *findable* by the person you want to play with. A friends-only lobby shows
    /// up as "Join Game" beside your name in their Steam friends list, so no code
    /// has to be read out and nothing has to be typed. Invisible is the trap here:
    /// it means "joinable but hidden from friends", which is the opposite of what
    /// a two-person session wants.
    ///
    /// **Unreliable sends.** Steam's reliable mode buffers for up to 200ms, and
    /// waiting for something that was already superseded is the wrong trade for
    /// rollback: every packet repeats the last sixteen frames, so a loss heals
    /// itself on the next one. Retransmission and ordering would only add delay to
    /// data that is about to be replaced.
    ///
    /// Callbacks need no pump of ours. <c>Game1.Update</c> already calls
    /// <c>SteamAPI.RunCallbacks()</c> every frame, so registering is enough.
    /// </summary>
    internal sealed class NetplayTransport : IDisposable
    {
        /// <summary>
        /// Two for now. Rollback needs every machine to agree on one input
        /// timeline, and n(n-1) paths with differing latency make the confirmed
        /// frame much harder to settle - so the mesh is a separate problem from
        /// making two peers work.
        /// </summary>
        public const int MaxMembers = 2;

        /// <summary>
        /// Channel to keep this mod's traffic off any other mod's. The existing
        /// multiplayer mod uses 0, and two mods reading each other's packets would
        /// be a confusing way to find out.
        /// </summary>
        private const int Channel = 41;

        private readonly byte[] _receive = new byte[NetplayPacket.MaxSize];
        private readonly List<CSteamID> _peers = new List<CSteamID>();

        private Callback<LobbyCreated_t> _lobbyCreated;
        private Callback<LobbyEnter_t> _lobbyEntered;
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<LobbyChatUpdate_t> _lobbyChanged;
        private Callback<P2PSessionRequest_t> _sessionRequested;

        private CSteamID _lobby;
        private bool _installed;

        /// <summary>Raised for each packet, with the peer it came from.</summary>
        public event Action<CSteamID, byte[], int> PacketReceived;

        /// <summary>Raised when the roster changes, including on join and leave.</summary>
        public event Action RosterChanged;

        public bool IsInLobby
        {
            get { return _lobby.IsValid(); }
        }

        public CSteamID Lobby
        {
            get { return _lobby; }
        }

        public IList<CSteamID> Peers
        {
            get { return _peers; }
        }

        public bool IsAvailable
        {
            get
            {
                try
                {
                    return SteamAPI.IsSteamRunning();
                }
                catch
                {
                    // Steamworks not initialised at all - the game was started
                    // outside Steam. Not an error, just no netplay.
                    return false;
                }
            }
        }

        public void Install()
        {
            if (_installed || !IsAvailable)
            {
                return;
            }

            _installed = true;
            _lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _joinRequested =
                Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _lobbyChanged = Callback<LobbyChatUpdate_t>.Create(OnLobbyChanged);
            _sessionRequested =
                Callback<P2PSessionRequest_t>.Create(OnSessionRequested);
        }

        /// <summary>
        /// Opens a friends-only lobby.
        /// </summary>
        public void CreateLobby()
        {
            if (!IsAvailable || IsInLobby)
            {
                return;
            }

            SteamMatchmaking.CreateLobby(
                ELobbyType.k_ELobbyTypeFriendsOnly,
                MaxMembers
            );
        }

        /// <summary>
        /// Opens Steam's own invite picker for this lobby.
        ///
        /// There has to be a way to actually reach somebody, and this is Steam's:
        /// it lists the friends who can be invited and sends the invitation, which
        /// arrives for them as a normal Steam notification. Building a friend
        /// picker of our own would mean reimplementing something the platform
        /// already does better, inside a pause menu drawn in a pixel font.
        ///
        /// It needs the Steam overlay to be enabled. When it is not, the lobby is
        /// still there and still friends-only, so the other side can join from the
        /// friends list instead - which is why this is offered rather than
        /// required.
        /// </summary>
        public void ShowInviteDialog()
        {
            if (!IsAvailable || !IsInLobby)
            {
                return;
            }

            SteamFriends.ActivateGameOverlayInviteDialog(_lobby);
        }

        public void LeaveLobby()
        {
            if (!IsInLobby)
            {
                return;
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                SteamNetworking.CloseP2PSessionWithUser(_peers[i]);
            }

            _peers.Clear();
            SteamMatchmaking.LeaveLobby(_lobby);
            _lobby = CSteamID.Nil;
            Raise(RosterChanged);
        }

        /// <summary>Sends to every peer. Unreliable, by design.</summary>
        public void Broadcast(byte[] payload, int length)
        {
            if (payload == null || length <= 0 || !IsInLobby)
            {
                return;
            }

            for (int i = 0; i < _peers.Count; i++)
            {
                SteamNetworking.SendP2PPacket(
                    _peers[i],
                    payload,
                    (uint)length,
                    EP2PSend.k_EP2PSendUnreliable,
                    Channel
                );
            }
        }

        /// <summary>
        /// Drains everything waiting. Called once per frame, before the simulation
        /// advances, so a packet that arrived is used on the frame it arrived
        /// rather than the next one.
        /// </summary>
        public void Pump()
        {
            if (!IsInLobby)
            {
                return;
            }

            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, Channel))
            {
                uint read;
                CSteamID from;
                if (!SteamNetworking.ReadP2PPacket(
                    _receive,
                    (uint)_receive.Length,
                    out read,
                    out from,
                    Channel
                ))
                {
                    return;
                }

                // A packet from somebody not on the roster is discarded rather than
                // parsed. Anyone can address a P2P packet at this user.
                if (read == 0 || !_peers.Contains(from))
                {
                    continue;
                }

                Action<CSteamID, byte[], int> handler = PacketReceived;
                if (handler != null)
                {
                    handler(from, _receive, (int)read);
                }
            }
        }

        public void Dispose()
        {
            LeaveLobby();

            DisposeCallback(ref _lobbyCreated);
            DisposeCallback(ref _lobbyEntered);
            DisposeCallback(ref _joinRequested);
            DisposeCallback(ref _lobbyChanged);
            DisposeCallback(ref _sessionRequested);
            _installed = false;
        }

        private static void DisposeCallback<T>(ref Callback<T> callback)
        {
            if (callback != null)
            {
                callback.Dispose();
                callback = null;
            }
        }

        private void OnLobbyCreated(LobbyCreated_t created)
        {
            if (created.m_eResult != EResult.k_EResultOK)
            {
                JumpKing.Program.crashLog.AddErrorMessage(
                    "Local Multiplayer could not create a lobby: " +
                    created.m_eResult
                );
                return;
            }

            _lobby = new CSteamID(created.m_ulSteamIDLobby);
            RefreshPeers();

            // The picker is not opened here. Choosing "Online" opens a lobby and
            // nothing more; throwing the Steam overlay up in front of somebody who
            // only changed a setting is startling, and it takes the screen away
            // from them before they asked for it. Inviting is its own button.
        }

        private void OnLobbyEntered(LobbyEnter_t entered)
        {
            _lobby = new CSteamID(entered.m_ulSteamIDLobby);
            RefreshPeers();
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            // Accepting an invite from the overlay. Steam has already decided this
            // user was invited, which is the whole point of an invisible lobby.
            SteamMatchmaking.JoinLobby(request.m_steamIDLobby);
        }

        private void OnLobbyChanged(LobbyChatUpdate_t update)
        {
            RefreshPeers();
        }

        private void OnSessionRequested(P2PSessionRequest_t request)
        {
            // Only for somebody already on the roster. Accepting anyone who asks is
            // how an uninvited peer would start receiving play data.
            if (_peers.Contains(request.m_steamIDRemote))
            {
                SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
            }
        }

        private void RefreshPeers()
        {
            _peers.Clear();
            if (!IsInLobby)
            {
                Raise(RosterChanged);
                return;
            }

            CSteamID self = SteamUser.GetSteamID();
            int count = SteamMatchmaking.GetNumLobbyMembers(_lobby);
            for (int i = 0; i < count; i++)
            {
                CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i);
                if (member != self)
                {
                    _peers.Add(member);
                }
            }

            Raise(RosterChanged);
        }

        private static void Raise(Action handler)
        {
            if (handler != null)
            {
                handler();
            }
        }
    }
}
