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
        private bool _created;

        /// <summary>Raised for each packet, with the peer it came from.</summary>
        public event Action<CSteamID, byte[], int> PacketReceived;

        /// <summary>Raised when the roster changes, including on join and leave.</summary>
        public event Action RosterChanged;

        /// <summary>
        /// Raised on entering a lobby. The flag says whether this machine created
        /// it, which decides who describes the session and who reads that
        /// description - the two sides of a lobby have nothing else to tell them
        /// apart.
        /// </summary>
        public event Action<bool> LobbyEntered;

        /// <summary>Raised when a lobby search finishes, with how many were found.</summary>
        public event Action<int> SearchFinished;

        /// <summary>
        /// Finds friends who are in a Jump King lobby right now.
        ///
        /// Asks Steam about each friend rather than searching the lobby list.
        /// <c>RequestLobbyList</c> looked like the obvious tool and returns nothing
        /// here: a friends-only lobby is visible to friends but deliberately absent
        /// from the lobby list, which is what makes it friends-only. Searching for
        /// one therefore always came back empty, and the button appeared to do
        /// nothing at all.
        ///
        /// <c>GetFriendGamePlayed</c> answers the question that was actually being
        /// asked - which of my friends is in a lobby I could join - and it answers
        /// it without the lobby having to be public to anybody else.
        /// </summary>
        public void FindLobbies()
        {
            if (!IsAvailable || IsInLobby || _searching)
            {
                return;
            }

            _searching = true;
            _found.Clear();

            try
            {
                CGameID thisGame = SteamUtils.GetAppID().m_AppId == 0
                    ? default(CGameID)
                    : new CGameID(SteamUtils.GetAppID());

                int count = SteamFriends.GetFriendCount(
                    EFriendFlags.k_EFriendFlagImmediate
                );

                for (int i = 0; i < count; i++)
                {
                    CSteamID friend = SteamFriends.GetFriendByIndex(
                        i,
                        EFriendFlags.k_EFriendFlagImmediate
                    );

                    FriendGameInfo_t info;
                    if (!SteamFriends.GetFriendGamePlayed(friend, out info))
                    {
                        continue;
                    }

                    // Playing something else, or playing this but not in a lobby.
                    if (info.m_gameID.m_GameID != thisGame.m_GameID ||
                        !info.m_steamIDLobby.IsValid())
                    {
                        continue;
                    }

                    // No tag check here. A lobby's data cannot be read from
                    // outside it until Steam has been asked for it, so filtering
                    // on our own marker rejected every lobby including ours - the
                    // list came back empty and the button looked broken.
                    //
                    // Asked for anyway, so the level shows next to the name once it
                    // arrives. A lobby that turns out not to be ours is refused at
                    // the handshake, with a reason, which is where a mismatched
                    // build or level is caught too.
                    SteamMatchmaking.RequestLobbyData(info.m_steamIDLobby);

                    _found.Add(new FoundLobby
                    {
                        Id = info.m_steamIDLobby,
                        HostName = SteamFriends.GetFriendPersonaName(friend),
                        LevelId = SteamMatchmaking.GetLobbyData(
                            info.m_steamIDLobby,
                            "level_id"
                        )
                    });
                }
            }
            catch
            {
                _found.Clear();
            }

            _searching = false;
            Raise(SearchFinished, _found.Count);
        }

        /// <summary>
        /// Opens Steam's friends list, where a friend already in a lobby shows a
        /// "Join Game" of Steam's own. The fallback when a search finds nothing,
        /// because Steam's own view of who is playing is more complete than a
        /// filtered lobby query.
        /// </summary>
        public void ShowFriendsOverlay()
        {
            if (IsAvailable)
            {
                SteamFriends.ActivateGameOverlay("friends");
            }
        }

        /// <summary>Marks a lobby as this mod's, so a search can find only ours.</summary>
        public const string LobbyTag = "eski4869_localmultiplayer";
        public const string LobbyTagValue = "1";

        private bool _searching;

        /// <summary>
        /// What the last search found: the lobby and who is hosting it.
        ///
        /// Kept rather than joined immediately, because the player has to be able
        /// to see the list and choose. Steam's own overlay was the first answer and
        /// it is not one: it shows friends, not lobbies, and there is nothing in it
        /// to press.
        /// </summary>
        public sealed class FoundLobby
        {
            public CSteamID Id;
            public string HostName;
            public string LevelId;
        }

        private readonly List<FoundLobby> _found = new List<FoundLobby>();

        public IList<FoundLobby> Found
        {
            get { return _found; }
        }

        public void JoinFound(int index)
        {
            if (index < 0 || index >= _found.Count || IsInLobby)
            {
                return;
            }

            SteamMatchmaking.JoinLobby(_found[index].Id);
        }


        /// <summary>Reads a value the host wrote about the session.</summary>
        public string ReadLobbyData(string key)
        {
            return IsInLobby ? SteamMatchmaking.GetLobbyData(_lobby, key) : null;
        }

        /// <summary>Describes the session, for joiners to read before connecting.</summary>
        public bool WriteLobbyData(string key, string value)
        {
            return IsInLobby && SteamMatchmaking.SetLobbyData(_lobby, key, value);
        }

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

        /// <summary>
        /// Whether this machine owns the lobby.
        ///
        /// Both machines have to agree which body is player one, and there is
        /// nothing symmetric that could tell them apart - each is "here" to itself.
        /// The lobby owner is the one fact Steam guarantees both sides read the
        /// same way, so it is what decides.
        /// </summary>
        public bool IsLobbyOwner
        {
            get
            {
                return IsInLobby &&
                    SteamMatchmaking.GetLobbyOwner(_lobby) == SteamUser.GetSteamID();
            }
        }

        /// <summary>This player's own Steam name.</summary>
        public static string LocalName
        {
            get
            {
                try
                {
                    return SteamFriends.GetPersonaName();
                }
                catch
                {
                    return "You";
                }
            }
        }

        /// <summary>The peer's Steam name, for saying who you are playing with.</summary>
        public string PeerName
        {
            get
            {
                return _peers.Count == 0
                    ? null
                    : SteamFriends.GetFriendPersonaName(_peers[0]);
            }
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
            _created = false;
            SteamFriends.ClearRichPresence();
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
            _created = true;

            // Tells Steam this lobby is joinable, which is what puts "Join Game"
            // beside your name in a friend's Steam list. Without it a friends-only
            // lobby is reachable only by an invitation somebody has to remember to
            // send.
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + _lobby.m_SteamID);

            RefreshPeers();
            Raise(LobbyEntered, true);

            // The picker is not opened here. Choosing "Online" opens a lobby and
            // nothing more; throwing the Steam overlay up in front of somebody who
            // only changed a setting is startling, and it takes the screen away
            // from them before they asked for it. Inviting is its own button.
        }

        private void OnLobbyEntered(LobbyEnter_t entered)
        {
            // Fires for the creator too, right after OnLobbyCreated, so the
            // already-announced case must not announce itself twice.
            var lobby = new CSteamID(entered.m_ulSteamIDLobby);
            bool alreadyKnown = _created && _lobby == lobby;

            _lobby = lobby;
            RefreshPeers();

            if (!alreadyKnown)
            {
                Raise(LobbyEntered, false);
            }
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

        private static void Raise<T>(Action<T> handler, T argument)
        {
            if (handler != null)
            {
                handler(argument);
            }
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
