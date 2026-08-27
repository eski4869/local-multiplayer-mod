using System.Collections.Generic;
using LocalMultiplayerMod;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Phase = LocalMultiplayerMod.SessionLifecycle.Phase;

namespace LocalMultiplayerMod.Tests
{
    /// <summary>
    /// Starting, refusing, timing out and ending a session.
    ///
    /// This part had no test of any kind while the correction logic had a harness,
    /// and both of the last two failures landed here - which is how it usually
    /// goes. A session that could not be started again for the rest of the run was
    /// not a hard bug; it was a bug in the half nobody was looking at.
    /// </summary>
    [TestClass]
    public class SessionLifecycleTests
    {
        private sealed class Effects : ISessionEffects
        {
            public readonly List<string> Log = new List<string>();
            public bool PeerPresent;
            public bool InLobby = true;

            public void SendHello()
            {
                Log.Add("hello");
            }

            public void SendStart()
            {
                Log.Add("start");
            }

            public void LeaveLobby()
            {
                InLobby = false;
                Log.Add("leave-lobby");
            }

            public void Notice(string message)
            {
                Log.Add("notice: " + message);
            }

            public void SetPeerPresent(bool present)
            {
                PeerPresent = present;
                Log.Add(present ? "peer-in" : "peer-out");
            }

            public void ResetSessionState()
            {
                Log.Add("reset");
            }
        }

        private static SessionLifecycle Hosting(Effects effects)
        {
            var life = new SessionLifecycle(effects);
            life.LobbyEntered(true);
            life.PeerArrived();
            return life;
        }

        [TestMethod]
        public void ARefusedSessionCanBeStartedAgain()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);

            life.Refuse("the other player is running a different version");

            // The reason to be refused is usually a version mismatch, and the
            // remedy for that is to update and try again. Parking in a state that
            // Host and Join both reject removed the only useful response to the
            // most likely fault, for the rest of the run.
            Assert.AreEqual(Phase.Idle, life.Current);
            Assert.IsTrue(life.CanBegin);
        }

        [TestMethod]
        public void ARefusalSaysWhyBeforeItLeaves()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);

            life.Refuse("different levels");

            int reason = effects.Log.IndexOf("notice: refused - different levels");
            int left = effects.Log.IndexOf("leave-lobby");

            Assert.IsTrue(reason >= 0, "no reason given");
            Assert.IsTrue(reason < left, "left before saying why");
        }

        [TestMethod]
        public void ARefusalDoesNotLeaveASecondPlayerBehind()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);
            life.HelloAccepted(true);
            Assert.IsTrue(effects.PeerPresent);

            life.Refuse("different levels");

            // A body with nobody driving it is worse than no body: it stands there
            // being mistaken for the other player.
            Assert.IsFalse(effects.PeerPresent);
        }

        [TestMethod]
        public void AHandshakeThatNeverCompletesEndsItself()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);

            bool alive = true;
            for (int i = 0; i <= SessionLifecycle.HandshakeLimitFrames; i++)
            {
                alive = life.Tick("them");
            }

            // Without the clock this greeted an unanswering peer sixty times a
            // second for ever, with the menu still saying a session was being
            // arranged.
            Assert.IsFalse(alive);
            Assert.AreEqual(Phase.Idle, life.Current);
        }

        [TestMethod]
        public void AHandshakeIsNotCutOffEarly()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);

            for (int i = 0; i < SessionLifecycle.HandshakeLimitFrames - 1; i++)
            {
                Assert.IsTrue(life.Tick("them"), "gave up at frame " + i);
            }

            // Ten seconds, because the other side may be loading a level. A
            // handshake window measured in a second or two calls a slow machine a
            // broken one.
            Assert.AreEqual(Phase.Handshaking, life.Current);
        }

        [TestMethod]
        public void AnEmptyLobbyWaitsIndefinitely()
        {
            var effects = new Effects();
            var life = new SessionLifecycle(effects);
            life.LobbyEntered(true);

            for (int i = 0; i < SessionLifecycle.HandshakeLimitFrames * 3; i++)
            {
                Assert.IsTrue(life.Tick("them"));
            }

            // This one is a person deciding whether to accept an invitation. It has
            // no business being on a clock.
            Assert.AreEqual(Phase.WaitingForPeer, life.Current);
        }

        [TestMethod]
        public void TheHandshakeClockRestartsForEachAttempt()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);

            for (int i = 0; i < SessionLifecycle.HandshakeLimitFrames - 10; i++)
            {
                life.Tick("them");
            }

            life.Leave(true);
            SessionLifecycle second = Hosting(new Effects());

            for (int i = 0; i < 20; i++)
            {
                Assert.IsTrue(second.Tick("them"), "the next attempt inherited a clock");
            }
        }

        [TestMethod]
        public void AnEmptyLobbyIsNotAGuestLeaving()
        {
            var effects = new Effects();
            var life = new SessionLifecycle(effects);
            life.LobbyEntered(true);

            // A host between opening a lobby and somebody accepting has an empty
            // lobby, and Steam reports that the same way it reports the last
            // person leaving one. Reading the second from the first closed the
            // lobby the instant it opened - "lobby open" and "your guest left" on
            // consecutive lines of a real log.
            life.PeerLeft(true);

            Assert.AreEqual(Phase.WaitingForPeer, life.Current);
            Assert.IsTrue(effects.InLobby, "the lobby closed itself");
            CollectionAssert.DoesNotContain(effects.Log, "notice: your guest left");
        }

        [TestMethod]
        public void AGuestLeavingDuringTheHandshakeIsNoticed()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);

            // And the case the widening was for: somebody who did arrive and then
            // went. Handled only during play once, so a guest refusing on an older
            // build and leaving went unnoticed until the handshake clock blamed a
            // disagreement that had not happened.
            life.PeerLeft(true);

            Assert.AreEqual(Phase.Idle, life.Current);
            CollectionAssert.Contains(effects.Log, "notice: your guest left");
        }

        [TestMethod]
        public void EveryStateReachesIdleAgain()
        {
            // The property behind both failures, checked directly rather than one
            // state at a time: whatever a session is doing, ending it must work.
            var states = new List<SessionLifecycle>();

            var a = new SessionLifecycle(new Effects());
            a.LobbyEntered(true);
            states.Add(a);

            var b = new SessionLifecycle(new Effects());
            b.LobbyEntered(false);
            states.Add(b);

            var c = new SessionLifecycle(new Effects());
            c.LobbyEntered(false);
            c.NeedsDifferentLevel();
            states.Add(c);

            var d = new SessionLifecycle(new Effects());
            d.LobbyEntered(true);
            d.PeerArrived();
            d.HelloAccepted(true);
            states.Add(d);

            for (int i = 0; i < states.Count; i++)
            {
                Phase was = states[i].Current;
                states[i].Leave(true);
                Assert.AreEqual(
                    Phase.Idle, states[i].Current, "stuck leaving " + was
                );
                Assert.IsTrue(states[i].CanBegin, "cannot restart after " + was);
            }
        }

        [TestMethod]
        public void OnlyTheHostDeclaresWhereTheSessionBegins()
        {
            var host = new Effects();
            SessionLifecycle hosting = Hosting(host);
            hosting.HelloAccepted(true);

            var guest = new Effects();
            var joining = new SessionLifecycle(guest);
            joining.LobbyEntered(false);
            joining.HelloAccepted(false);

            // Two machines cannot both decide which frame the session starts on.
            // Both deciding is the same as neither.
            CollectionAssert.Contains(host.Log, "start");
            CollectionAssert.DoesNotContain(guest.Log, "start");
        }

        [TestMethod]
        public void AGuestWaitingForALevelIsStillInTheSession()
        {
            var effects = new Effects();
            var life = new SessionLifecycle(effects);
            life.LobbyEntered(false);
            life.NeedsDifferentLevel();

            // Lobby membership outlasting the level change is the whole reason this
            // state exists: changing level restarts the run, so the join cannot
            // simply be held open by the level being right already.
            Assert.IsTrue(effects.InLobby);

            life.LevelReady();
            Assert.AreEqual(Phase.Handshaking, life.Current);
        }

        [TestMethod]
        public void EndingTwiceIsNotAnnouncedTwice()
        {
            var effects = new Effects();
            SessionLifecycle life = Hosting(effects);
            life.HelloAccepted(true);

            life.Leave(true);
            int after = effects.Log.Count;
            life.Leave(true);

            // A session ends once. The lobby callback and the player's own action
            // both arrive when a lobby closes, and reporting each of them says the
            // session ended twice.
            Assert.AreEqual(after, effects.Log.Count);
        }
    }
}
