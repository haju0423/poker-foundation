using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using Poker.Application;
using Poker.Transport;
using UnityEngine;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemTcpIntegrationTests
    {
        private HoldemTcpServer server;
        private readonly HoldemTcpClient[] clients = new HoldemTcpClient[4];
        private readonly HoldemClientIdentity[] identities = new HoldemClientIdentity[4];
        private readonly List<HoldemWireResponse>[] replies = Enumerable.Range(0, 4).Select(_ => new List<HoldemWireResponse>()).ToArray();

        [SetUp]
        public void Setup() => Create();

        private void Create(HoldemConfig config = null, IRandomSource random = null, HoldemUtterancePolicy utterancePolicy = null,
            int seatCapacity = HoldemRoom.Capacity)
        {
            for (int i = 0; i < 4; i++) { identities[i] = new HoldemClientIdentity("참가자 " + (i + 1)); replies[i].Clear(); }
            server = new HoldemTcpServer(identities[0], config ?? new HoldemConfig(100, 1, 2), new SeatId(1),
                random ?? new StableRandom(), utterancePolicy: utterancePolicy, seatCapacity: seatCapacity);
        }

        [TearDown]
        public void Cleanup()
        {
            for (int i = 0; i < 4; i++) { clients[i]?.Dispose(); clients[i] = null; }
            server?.Dispose(); server = null;
        }

        private void Connect(int index)
        {
            clients[index] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[index]).GetAwaiter().GetResult();
            Until(() => clients[index].IsAdmitted && clients[index].Latest != null);
        }
        private void ConnectAll()
        {
            for (int i = 0; i < 4; i++) Connect(i);
            for (int i = 0; i < 4; i++) Command(i, new HoldemWireRequest { type = "ready", ready = true });
            Until(() => clients.All(c => c.Latest.members.Length == 4 && c.Latest.members.All(m => m.ready)));
        }
        private void Start()
        {
            long version = clients[0].Latest.hasGame ? clients[0].Latest.game.version : 0;
            var ack = Command(0, new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = version });
            Assert.That(ack.accepted, Is.True);
            Until(() => clients.All(c => c.Latest.hasGame && c.Latest.game.version == ack.receipt.version));
        }
        private void Tick()
        {
            server.Pump();
            for (int i = 0; i < 4; i++)
            {
                if (clients[i] == null) continue;
                clients[i].Poll();
                while (clients[i].TryReadResponse(out var response)) replies[i].Add(response);
            }
        }
        private void Until(Func<bool> condition, int timeout = 5000)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.ElapsedMilliseconds < timeout) { Tick(); Thread.Sleep(1); }
            Assert.That(condition(), Is.True, "Loopback operation exceeded its bounded wait.");
        }
        private HoldemWireResponse Command(int index, HoldemWireRequest request)
        {
            int start = replies[index].Count;
            string id = clients[index].Send(request); Assert.That(id, Is.Not.Null);
            Until(() => replies[index].Skip(start).Any(r => r.id == id));
            return replies[index].Skip(start).First(r => r.id == id);
        }
        private HoldemWireRequest Passive(int actor)
        {
            var game = clients[actor].Latest.game;
            return new HoldemWireRequest { type = "act", handId = game.handId, version = game.version, action = game.legal.check ? 1 : 2 };
        }
        private void ActCurrent(bool fold = false)
        {
            int actor = clients[0].Latest.game.currentSeat - 1;
            var request = Passive(actor); if (fold) request.action = 0;
            var ack = Command(actor, request); Assert.That(ack.accepted, Is.True, ack.error);
            Until(() => clients.All(c => c.Latest.game.version == ack.receipt.version));
        }

        private HoldemWireRequest Speech(int index, string text) => new HoldemWireRequest
        {
            type = "utterance", id = Guid.NewGuid().ToString("N"), handId = clients[index].Latest.game.handId,
            windowId = clients[index].Latest.ownUtterances.windowId, street = clients[index].Latest.game.street,
            text = text, version = -1 // Deliberately not a poker-state freshness precondition.
        };

        [Test]
        public void NetworkSpeechIsOptionalOwnOnlyAndNeverChangesBettingState()
        {
            ConnectAll(); Start();
            Assert.That(clients.All(c => !c.Latest.hasOwnUtterances), Is.True,
                "Unity may deserialize disabled optional objects as empty objects; the has flag is authoritative.");
            var off = Command(1, new HoldemWireRequest { type = "utterance", handId = clients[1].Latest.game.handId,
                windowId = Guid.NewGuid().ToString("N"), text = "꺼진 기능" });
            Assert.That(off.accepted, Is.False);
            Assert.That(off.utteranceError, Is.EqualTo("Disabled"));
            Cleanup(); Create(utterancePolicy: new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            ConnectAll(); Start();
            var delayed = Speech(1, "접수 원문 A");
            ActCurrent();
            long version = clients[1].Latest.game.version, pot = clients[1].Latest.game.pot;
            int actor = clients[1].Latest.game.currentSeat;
            var accepted = Command(1, delayed);
            Assert.That(accepted.type, Is.EqualTo("utterance-receipt"));
            Assert.That(accepted.accepted, Is.True);
            Assert.That(accepted.hasReceipt, Is.False);
            Until(() => clients[1].Latest.ownUtterances.entries.Length == 1);
            Assert.That(clients[1].Latest.game.version, Is.EqualTo(version));
            Assert.That(clients[1].Latest.game.pot, Is.EqualTo(pot));
            Assert.That(clients[1].Latest.game.currentSeat, Is.EqualTo(actor));
            Assert.That(clients[1].Latest.ownUtterances.entries[0].text, Is.EqualTo(delayed.text));
            Assert.That(Command(2, Speech(2, "접수 원문 B")).accepted, Is.True);
            Until(() => clients[2].Latest.ownUtterances.entries.Length == 1);
            foreach (int i in new[] { 0, 2, 3 }) StringAssert.DoesNotContain(delayed.text, JsonUtility.ToJson(clients[i].Latest));
            StringAssert.DoesNotContain("접수 원문 B", JsonUtility.ToJson(clients[1].Latest));
            StringAssert.DoesNotContain("ordinal", JsonUtility.ToJson(clients[1].Latest).ToLowerInvariant());
            StringAssert.DoesNotContain("ordinal", JsonUtility.ToJson(accepted).ToLowerInvariant());
            Assert.That(Command(1, delayed).accepted, Is.True);
            var changed = Speech(1, "다른 원문"); changed.id = delayed.id;
            Assert.That(Command(1, changed).utteranceError, Is.EqualTo("CommandConflict"));
            Assert.That(Command(2, delayed).utteranceError, Is.EqualTo("CommandConflict"), "A request cannot assume its original sender.");
            for (int i = 0; i < 3 && !clients[0].Latest.game.hasResult; i++) ActCurrent(true);
            var feed = server.ReadPendingUtterances();
            Assert.That(feed.Count, Is.EqualTo(1));
            Assert.That(feed[0].Count, Is.EqualTo(2));
            Assert.That(feed[0].GetEntry(0).Speaker.Value, Is.EqualTo(2));
            Assert.That(feed[0].GetEntry(1).Speaker.Value, Is.EqualTo(3));
            Assert.That(Command(1, new HoldemWireRequest { type = "read-utterance-batches",
                handId = delayed.handId }).error, Is.EqualTo("UnknownMessage"));
            Start();
            Assert.That(server.ReadPendingUtterances()[0], Is.SameAs(feed[0]));
            Assert.That(clients.All(c => c.Latest.ownUtterances.entries.Length == 0), Is.True);
            Assert.That(server.AcknowledgeUtteranceBatch(feed[0].WindowId), Is.True);
            Assert.That(server.ReadPendingUtterances(), Is.Empty);
        }

        [Test]
        public void SpeechLostReceiptReconnectDoesNotAddADuplicate()
        {
            Cleanup(); Create(utterancePolicy: new HoldemUtterancePolicy(128, 2, HoldemUtteranceSeats.Active));
            ConnectAll(); Start();
            var request = Speech(1, "응답을 놓친 원문");
            Assert.That(clients[1].Send(request), Is.Not.Null);
            var wait = Stopwatch.StartNew();
            while (server.PendingInputCount == 0 && wait.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Assert.That(server.PendingInputCount, Is.GreaterThan(0));
            server.Pump(); // Authoritative acceptance; deliberately do not poll/read the sender's response.
            clients[1].Dispose(); clients[1] = null;
            Until(() => clients[0].Latest.paused);
            Connect(1); Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(clients[1].Latest.ownUtterances.entries.Length, Is.EqualTo(1));
            Assert.That(Command(1, request).accepted, Is.True);
            Assert.That(clients[1].Latest.ownUtterances.entries.Length, Is.EqualTo(1));
            for (int i = 0; i < 3; i++) ActCurrent(true);
            Assert.That(server.ReadPendingUtterances()[0].Count, Is.EqualTo(1));
        }

        [Test]
        public void WirePolicyRejectsAnUnboundedHistoryBeforeOpeningAListener()
        {
            Assert.Throws<ArgumentException>(() => new HoldemTcpServer(new HoldemClientIdentity("버퍼 검사"),
                new HoldemConfig(100, 1, 2), new SeatId(1), new StableRandom(),
                utterancePolicy: new HoldemUtterancePolicy(4096, 32, HoldemUtteranceSeats.Active)));
        }

        [Test]
        public void ThreeStreetEscapedTextHistoryStillFitsTheBoundedWireFrame()
        {
            Cleanup(); Create(utterancePolicy: new HoldemUtterancePolicy(1024, 1, HoldemUtteranceSeats.Active));
            ConnectAll(); Start();
            string text = new string('"', 1024);
            for (int street = 0; street < 3; street++)
            {
                Assert.That(clients[1].Latest.game.street, Is.EqualTo(street));
                Assert.That(Command(1, Speech(1, text)).accepted, Is.True);
                Until(() => clients[1].Latest.ownUtterances.entries.Length == street + 1);
                for (int actor = 0; actor < 4; actor++) ActCurrent();
            }
            Assert.That(clients[1].Latest.game.street, Is.EqualTo((int)HoldemStreet.River));
            Assert.That(clients[1].Latest.ownUtterances.canSubmit, Is.False);
            Assert.That(clients[1].Latest.ownUtterances.entries.Length, Is.EqualTo(3));
            Assert.That(clients.All(c => c.IsAdmitted), Is.True);
            var encoded = HoldemFrameCodec.Encode(JsonUtility.ToJson(new HoldemWireResponse
            { type = "state", state = clients[1].Latest }));
            Assert.That(encoded.Length, Is.LessThanOrEqualTo(HoldemFrameCodec.MaximumBytes + 4));
        }

        [Test]
        public void EliminatedSocketCanLeaveWhileThreeFundedPlayersContinueAndRejoinItsOwnSeat()
        {
            Cleanup(); Create(random: new SeededRandom(1295)); ConnectAll(); Start();
            var initial = clients[3].Latest.game;
            Assert.That(Command(3, new HoldemWireRequest { type = "act", handId = initial.handId,
                version = initial.version, action = 4, target = 100 }).accepted, Is.True);
            Until(() => clients[0].Latest.game.currentSeat == 1);
            Assert.That(clients[0].Latest.game.seats.Single(s => s.seat == 4).stack, Is.Zero);
            clients[3].Dispose(); Until(() => clients[0].Latest.paused);
            Connect(3); Until(() => clients.All(c => !c.Latest.paused));
            ActCurrent(true); ActCurrent(); ActCurrent(true);
            Until(() => clients.All(c => c.Latest.game.hasResult));
            int eliminated = clients[0].Latest.game.seats.Single(s => s.stack == 0).seat - 1;
            Assert.That(eliminated, Is.Not.Zero);
            clients[eliminated].Dispose(); clients[eliminated] = null;
            Until(() => clients[0].Latest.members.Single(m => m.seat == eliminated + 1).connected == false);
            Assert.That(clients[0].Latest.paused, Is.False);
            var next = Command(0, new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"),
                version = clients[0].Latest.game.version });
            Assert.That(next.accepted, Is.True);
            Until(() => clients.Where(c => c != null).All(c => c.Latest.game.handNumber == 2));
            Assert.That(clients[0].Latest.game.seats.Count(s => s.dealtIn), Is.EqualTo(3));
            int actor = clients[0].Latest.game.currentSeat - 1;
            Assert.That(Command(actor, Passive(actor)).accepted, Is.True);
            Connect(eliminated);
            Assert.That(clients[eliminated].Latest.viewerSeat, Is.EqualTo(eliminated + 1));
            Assert.That(clients[eliminated].Latest.game.seats.Single(s => s.viewer).visibleCards, Is.Empty);
            Assert.That(clients[eliminated].Latest.game.hasLegal, Is.False);
            Assert.That(clients[eliminated].Latest.members.Length, Is.EqualTo(4));
        }

        [Test]
        public void RetiredSocketCannotDisconnectTheNewOccupantEvenWhenAnotherGuestIsPaused()
        {
            ConnectAll();
            clients[3].Dispose();
            Until(() => clients[0].Latest.paused);
            var retired = clients[1];
            try
            {
                Assert.That(Command(1, new HoldemWireRequest { type = "leave" }).accepted, Is.True);
                Until(() => clients[0].Latest.members.Length == 3);
                identities[1] = new HoldemClientIdentity("교체 참가자"); clients[1] = null; Connect(1);
                Until(() => clients[0].Latest.members.Length == 4);
                Assert.That(clients[1].Latest.viewerSeat, Is.EqualTo(2));
                retired.Dispose();
                clients[3] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[3]).GetAwaiter().GetResult();
                Until(() => clients.All(c => c.IsAdmitted && !c.Latest.paused));
                Assert.That(clients[0].Latest.members.Single(m => m.seat == 2).connected, Is.True);
                Command(1, new HoldemWireRequest { type = "ready", ready = true }); Start();
                Assert.That(clients.All(c => c.Latest.game.handNumber == 1), Is.True);
            }
            finally { retired.Dispose(); }
        }

        [Test]
        public void AdmissionHistoryIsBoundedWithoutPreventingExistingSeatRecovery()
        {
            ConnectAll();
            var originalRetiredIdentity = identities[1];
            Command(1, new HoldemWireRequest { type = "leave" }); clients[1].Dispose(); clients[1] = null;
            // Four original identities plus 252 successive visitors fill the room-lifetime history.
            for (int visitor = 0; visitor < 252; visitor++)
            {
                identities[1] = new HoldemClientIdentity("친구 " + visitor); Connect(1);
                Assert.That(Command(1, new HoldemWireRequest { type = "leave" }).accepted, Is.True);
                clients[1].Dispose(); clients[1] = null;
            }
            Until(() => clients[0].Latest.members.Length == 3 && !clients[0].Latest.paused);
            long revision = clients[0].Latest.revision;
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, new HoldemClientIdentity("한도 이후")).GetAwaiter().GetResult();
            Until(() => clients[1].AdmissionError != null);
            Assert.That(clients[1].AdmissionError, Is.EqualTo("AdmissionLimit"));
            Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            Assert.That(clients[0].Latest.members.Length, Is.EqualTo(3));
            clients[1].Dispose();
            clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, originalRetiredIdentity).GetAwaiter().GetResult();
            Until(() => clients[1].AdmissionError != null);
            Assert.That(clients[1].AdmissionError, Is.EqualTo("LobbyLeft"));
            clients[2].Dispose(); Until(() => clients[0].Latest.paused);
            clients[2] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[2]).GetAwaiter().GetResult();
            Until(() => clients[2].IsAdmitted && !clients[0].Latest.paused);
            Assert.That(clients[2].Latest.viewerSeat, Is.EqualTo(3));
            Assert.That(clients[0].Latest.members.Length, Is.EqualTo(3));
        }

        [Test]
        public void LobbyLeaveIsIdempotentAndAFreshIdentityGetsOnlyTheEmptySeat()
        {
            ConnectAll();
            Assert.That(Command(0, new HoldemWireRequest { type = "leave" }).error, Is.EqualTo("HostCannotLeave"));
            var leave = new HoldemWireRequest { type = "leave" };
            Assert.That(Command(1, leave).accepted, Is.True);
            Until(() => clients[0].Latest.members.Length == 3);
            long revision = clients[0].Latest.revision;
            Assert.That(Command(1, leave).accepted, Is.True);
            Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            Assert.That(Command(1, new HoldemWireRequest { type = "ready", ready = true }).error, Is.EqualTo("LobbyLeft"));
            clients[1].Dispose(); clients[1] = null;
            Until(() => clients[0].Latest.members.Length == 3 && !clients[0].Latest.paused);
            identities[1] = new HoldemClientIdentity("새 친구"); Connect(1);
            Until(() => clients.All(c => c.Latest.members.Length == 4));
            Assert.That(clients[1].Latest.viewerSeat, Is.EqualTo(2));
            Assert.That(clients[2].Latest.viewerSeat, Is.EqualTo(3));
            Assert.That(clients[3].Latest.viewerSeat, Is.EqualTo(4));
            Assert.That(clients[1].Latest.members.Single(m => m.seat == 2).ready, Is.False);
            Command(1, new HoldemWireRequest { type = "ready", ready = true }); Start();
            long version = clients[0].Latest.game.version;
            Assert.That(Command(1, new HoldemWireRequest { type = "leave" }).error, Is.EqualTo("AlreadyStarted"));
            Assert.That(clients[0].Latest.game.version, Is.EqualTo(version));
            Assert.That(clients[0].Latest.members.Length, Is.EqualTo(4));
        }

        [Test]
        public void LostLeaveReceiptCanBeConfirmedAfterReconnectWithoutReservingASeatAgain()
        {
            ConnectAll();
            var previous = clients[1];
            previous.Send(new HoldemWireRequest { type = "leave" }); clients[1] = null;
            try
            {
                Until(() => clients[0].Latest.members.Length == 3);
                previous.Dispose();
                clients[1] = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
                Until(() => clients[1].AdmissionError != null);
                Assert.That(clients[1].AdmissionError, Is.EqualTo("LobbyLeft"));
                Assert.That(clients[0].Latest.members.Length, Is.EqualTo(3));
                Assert.That(clients[0].Latest.paused, Is.False);
            }
            finally { previous.Dispose(); }
        }

        [Test]
        public void FourRealSocketsReceivePrivateCardsAndCompleteShowdownThenNextHand()
        {
            ConnectAll(); Start();
            for (int i = 0; i < 4; i++)
            {
                var state = clients[i].Latest;
                Assert.That(state.viewerSeat, Is.EqualTo(i + 1));
                Assert.That(state.game.seats.Sum(s => s.visibleCards.Length), Is.EqualTo(2));
                Assert.That(state.game.seats[i].visibleCards.Length, Is.EqualTo(2));
                Assert.That(state.game.hasLegal, Is.EqualTo(i == 3));
                string json = JsonUtility.ToJson(state);
                foreach (var identity in identities) Assert.That(json.Contains(identity.AdmissionRequest().admissionKey), Is.False);
            }
            for (int step = 0; step < 100 && !clients[0].Latest.game.hasResult; step++) ActCurrent();
            foreach (var client in clients)
            {
                var game = client.Latest.game;
                Assert.That(game.hasResult, Is.True);
                Assert.That(game.board.Length, Is.EqualTo(5));
                Assert.That(game.seats.All(s => s.visibleCards.Length == 2 && s.revealedBestCards.Length == 5), Is.True);
                Assert.That(game.seats.Sum(s => s.stack), Is.EqualTo(400));
            }
            string oldHand = clients[0].Latest.game.handId;
            Start();
            Assert.That(clients[0].Latest.game.handId, Is.Not.EqualTo(oldHand));
            foreach (var client in clients)
                Assert.That(client.Latest.game.seats.Sum(s => s.visibleCards.Length), Is.EqualTo(2));
        }

        [Test]
        public void SocketBindingRejectsOtherSeatsAndDuplicateCommandsPayOnlyOnce()
        {
            ConnectAll(); Start(); var before = clients[3].Latest;
            var request = Passive(3);
            Assert.That(Command(1, request).receipt.error, Is.EqualTo("WrongTurn"));
            Assert.That(clients[1].Latest.game.version, Is.EqualTo(before.game.version));
            request.id = Guid.NewGuid().ToString("N");
            var accepted = Command(3, request); Assert.That(accepted.accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == accepted.receipt.version));
            long stack = clients[3].Latest.game.seats[3].stack;
            var retry = Command(3, request);
            Assert.That(retry.receipt.version, Is.EqualTo(accepted.receipt.version));
            Assert.That(clients[3].Latest.game.seats[3].stack, Is.EqualTo(stack));
            request.action = 0;
            Assert.That(Command(3, request).receipt.error, Is.EqualTo("CommandConflict"));
            var hostOnly = Command(2, new HoldemWireRequest { type = "start", handId = Guid.NewGuid().ToString("N"), version = accepted.receipt.version });
            Assert.That(hostOnly.error, Is.EqualTo("HostOnly"));
        }

        [Test]
        public void DisconnectPausesAndSameIdentityRecoversCardsWithoutFoldingOrResetting()
        {
            ConnectAll(); Start(); var request = Passive(3);
            var accepted = Command(3, request);
            Until(() => clients.All(c => c.Latest.game.version == accepted.receipt.version));
            var before = clients[3].Latest;
            clients[3].Dispose(); clients[3] = null;
            Until(() => clients[0].Latest.paused);
            var paused = Command(0, Passive(0)); Assert.That(paused.error, Is.EqualTo("Paused"));
            Connect(3); Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(clients[3].Latest.viewerSeat, Is.EqualTo(4));
            Assert.That(clients[3].Latest.game.version, Is.EqualTo(before.game.version));
            Assert.That(clients[3].Latest.game.seats[3].visibleCards, Is.EqualTo(before.game.seats[3].visibleCards));
            Assert.That(clients[3].Latest.game.seats[3].stack, Is.EqualTo(before.game.seats[3].stack));
            Assert.That(Command(3, request).receipt.version, Is.EqualTo(accepted.receipt.version));
        }

        [Test]
        public void LosingTheFirstAdmissionResponseDoesNotConsumeAnExtraSeat()
        {
            Connect(0);
            var socket = new TcpClient(); socket.Connect(server.Endpoint);
            using (var unread = new HoldemTcpChannel(socket))
            {
                unread.TrySend(JsonUtility.ToJson(identities[1].AdmissionRequest()));
                Until(() => clients[0].Latest.members.Length == 2);
                // Drop all response bytes without applying ADMITTED to the client identity.
                Assert.That(identities[1].SessionId, Is.Null); Assert.That(identities[1].Seat, Is.Zero);
            }
            Until(() => clients[0].Latest.members[1].connected == false);
            Connect(1);
            Assert.That(clients[1].Latest.viewerSeat, Is.EqualTo(2));
            Assert.That(clients[1].Latest.members.Length, Is.EqualTo(2));
            Connect(2); Assert.That(clients[2].Latest.viewerSeat, Is.EqualTo(3));
        }

        [Test]
        public void ASecondLiveSocketCannotTakeOverAnAdmittedSeat()
        {
            ConnectAll(); Start();
            using (var duplicate = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult())
            {
                HoldemWireResponse rejection = null;
                Until(() =>
                {
                    duplicate.Poll();
                    while (duplicate.TryReadResponse(out var response)) if (response.error == "MemberStillConnected") rejection = response;
                    return rejection != null;
                });
                Assert.That(duplicate.IsAdmitted, Is.False);
                Assert.That(clients[1].IsAdmitted, Is.True);
                Assert.That(clients[1].Latest.viewerSeat, Is.EqualTo(2));
            }
        }

        [Test]
        public void ReconnectRetriesOnTheSameSocketAfterTheOldBindingFinallyCloses()
        {
            ConnectAll(); Start(); var before = clients[1].Latest;
            var reconnecting = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[1]).GetAwaiter().GetResult();
            try
            {
                bool rejectedWhileLive = false;
                Until(() =>
                {
                    reconnecting.Poll();
                    while (reconnecting.TryReadResponse(out var reply)) rejectedWhileLive |= reply.error == "MemberStillConnected";
                    return rejectedWhileLive;
                });
                Assert.That(reconnecting.IsAdmitted, Is.False);
                clients[1].Dispose(); clients[1] = reconnecting;
                Until(() => reconnecting.IsAdmitted && reconnecting.Latest != null && !reconnecting.Latest.paused);
                Assert.That(reconnecting.Latest.viewerSeat, Is.EqualTo(before.viewerSeat));
                Assert.That(reconnecting.Latest.members.Length, Is.EqualTo(4));
                Assert.That(reconnecting.Latest.game.version, Is.EqualTo(before.game.version));
                Assert.That(reconnecting.Latest.game.seats[1].visibleCards, Is.EqualTo(before.game.seats[1].visibleCards));
            }
            finally { if (clients[1] != reconnecting) reconnecting.Dispose(); }
        }

        [Test]
        public void ServerInitiatedClosePausesBeforeALaterPeersQueuedActionInTheSamePump()
        {
            ConnectAll(); Start(); Tick();
            long version = clients[3].Latest.game.version;
            var action = Passive(3); action.id = Guid.NewGuid().ToString("N");
            clients[0].Send(new HoldemWireRequest { type = "sync", id = "not-a-command-id" });
            clients[3].Send(action);
            var elapsed = Stopwatch.StartNew();
            while (server.PendingInputCount < 2 && elapsed.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            Assert.That(server.PendingInputCount, Is.GreaterThanOrEqualTo(2));
            server.Pump();
            Until(() => replies[3].Any(r => r.id == action.id));
            Assert.That(replies[3].Last(r => r.id == action.id).error, Is.EqualTo("Paused"));
            // The command reply and broadcast state are separate frames.
            Until(() => clients[3].Latest.paused && !clients[3].Latest.members[0].connected);
            Assert.That(clients[3].Latest.game.version, Is.EqualTo(version));
            Assert.That(clients[3].Latest.paused, Is.True);
            Assert.That(clients[3].Latest.members[0].connected, Is.False);
        }

        [Test]
        public void ExplicitSyncHasACorrelatedResponseWithoutChangingGameState()
        {
            ConnectAll(); Start(); var before = clients[2].Latest;
            var response = Command(2, new HoldemWireRequest { type = "sync" });
            Assert.That(response.type, Is.EqualTo("state"));
            Assert.That(response.state.revision, Is.EqualTo(before.revision));
            Assert.That(response.state.viewerSeat, Is.EqualTo(3));
            Assert.That(response.state.game.version, Is.EqualTo(before.game.version));
            Assert.Throws<ArgumentException>(() => clients[2].Send(new HoldemWireRequest { type = "ping" }));
        }

        [Test]
        public void BufferedAdmissionCannotMakeAClosedSocketActiveButKeepsResumeIdentity()
        {
            using (var delayed = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[0]).GetAwaiter().GetResult())
            {
                delayed.Poll(); // Adopt the connection, then deliberately stop polling its responses.
                Until(() => delayed.BufferedMessageCount >= 2);
                Assert.That(identities[0].SessionId, Is.Null);
                server.Dispose();
                var elapsed = Stopwatch.StartNew();
                while (!delayed.IsClosed && elapsed.ElapsedMilliseconds < 5000) Thread.Sleep(1);
                Assert.That(delayed.IsClosed, Is.True);
                delayed.Poll();
                Assert.That(delayed.IsAdmitted, Is.False);
                Assert.That(identities[0].SessionId, Is.Not.Null);
                Assert.That(identities[0].Seat, Is.EqualTo(1));
            }
        }

        [Test]
        public void MalformedFramesCannotMutateTheRoomOrExposeState()
        {
            Connect(0); long revision = clients[0].Latest.revision;
            using (var socket = new TcpClient())
            {
                socket.Connect(server.Endpoint);
                socket.GetStream().Write(new byte[] { 255, 255, 255, 255 }, 0, 4);
                using (var channel = new HoldemTcpChannel(socket))
                {
                    Until(() => channel.IsClosed);
                    Assert.That(channel.TryRead(out _), Is.False);
                }
            }
            Assert.That(clients[0].Latest.revision, Is.EqualTo(revision));
            Assert.That(clients[0].Latest.members.Length, Is.EqualTo(1));
        }

        [Test]
        public void ReconnectToAReplacementServerRejectsTheOldSession()
        {
            Connect(0); clients[0].Dispose(); clients[0] = null; server.Dispose();
            server = new HoldemTcpServer(new HoldemClientIdentity("새 호스트"), new HoldemConfig(100, 1, 2), new SeatId(1), new StableRandom());
            using (var old = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port, identities[0]).GetAwaiter().GetResult())
            {
                HoldemWireResponse rejection = null;
                Until(() => { old.Poll(); while (old.TryReadResponse(out var response)) rejection = response; return rejection != null; });
                Assert.That(rejection.error, Is.EqualTo("SessionGone"));
                Assert.That(old.AdmissionError, Is.EqualTo("SessionGone"));
                Assert.That(old.IsClosed, Is.True);
                Assert.That(old.IsAdmitted, Is.False);
                Assert.That(old.Latest, Is.Null);
            }
        }

        [Test]
        public void WildcardOrUnapprovedLanBindingIsRejected()
        {
            Assert.Throws<ArgumentException>(() => new HoldemTcpServer(identities[0], new HoldemConfig(100, 1, 2), new SeatId(1),
                new StableRandom(), new IPEndPoint(IPAddress.Any, 0), true));
            Assert.Throws<ArgumentException>(() => new HoldemTcpServer(identities[0], new HoldemConfig(100, 1, 2), new SeatId(1),
                new StableRandom(), new IPEndPoint(IPAddress.Parse("192.168.1.99"), 0)));
        }

        [Test]
        public void CancelledAsyncConnectionWithoutAnOwnerNeverClaimsARoomSeat()
        {
            Connect(0);
            using (var unused = HoldemTcpClient.ConnectAsync(server.Endpoint.Address, server.Endpoint.Port,
                new HoldemClientIdentity("취소한 참가자")).GetAwaiter().GetResult())
            {
                for (int i = 0; i < 10; i++) { server.Pump(); clients[0].Poll(); Thread.Sleep(1); }
                Assert.That(unused.IsAdmitted, Is.False);
                Assert.That(clients[0].Latest.members.Length, Is.EqualTo(1));
            }
            for (int i = 1; i < 4; i++) Connect(i);
            Until(() => clients.All(c => c.Latest.members.Length == 4));
            Assert.That(clients[0].Latest.members.All(m => m.connected), Is.True);
        }

        [Test]
        public void CommunityDealAndRevealAreSeparateHostRequestsOnAllFourConnections()
        {
            Cleanup(); Create(new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                dealPolicy: HoldemDealPolicy.WaitForHost));
            ConnectAll(); Start();
            int reveals = 0;
            for (int step = 0; step < 100 && !clients[0].Latest.game.hasResult; step++)
            {
                var game = clients[0].Latest.game;
                if (game.dealPending)
                {
                    var deal = new HoldemWireRequest { type = "deal", handId = game.handId,
                        windowId = game.dealWindowId, street = game.dealStreet, version = game.version };
                    Assert.That(Command(1, deal).error, Is.EqualTo("HostOnly"));
                    deal.id = Guid.NewGuid().ToString("N");
                    var ack = Command(0, deal); Assert.That(ack.accepted, Is.True);
                    Until(() => clients.All(c => c.Latest.game.version == ack.receipt.version));
                    Assert.That(clients.All(c => c.Latest.game.revealPending && !c.Latest.game.hasLegal), Is.True);
                    Assert.That(Command(0, deal).receipt.version, Is.EqualTo(ack.receipt.version));
                    Assert.That(clients[0].Latest.game.board.Length, Is.EqualTo(++reveals + 2));
                }
                else if (game.revealPending)
                {
                    var ack = Command(0, new HoldemWireRequest { type = "reveal", handId = game.handId,
                        version = game.version, street = game.street });
                    Assert.That(ack.accepted, Is.True);
                    Until(() => clients.All(c => c.Latest.game.version == ack.receipt.version));
                }
                else ActCurrent();
            }
            Assert.That(reveals, Is.EqualTo(3));
            Assert.That(clients.All(c => c.Latest.game.hasResult), Is.True);
        }

        [Test]
        public void AllInSidePotsAndExplicitOddChipSettlementMatchForEverySocket()
        {
            ConnectAll(); Start();
            for (int i = 0; i < 3; i++) ActCurrent(true);
            Start();
            var actions = new[] { 4, 2, 2, 0 };
            for (int i = 0; i < actions.Length; i++)
            {
                var game = clients[i].Latest.game;
                var ack = Command(i, new HoldemWireRequest { type = "act", handId = game.handId,
                    version = game.version, action = actions[i], target = i == 0 ? 100 : 0 });
                Assert.That(ack.accepted, Is.True);
                Until(() => clients.All(c => c.Latest.game.version == ack.receipt.version));
            }
            var pending = clients[0].Latest.game;
            Assert.That(pending.settlementState, Is.EqualTo(1));
            Assert.That(pending.hasResult, Is.False);
            int zeroStack = Array.FindIndex(pending.seats, s => s.stack == 0 && s.seat != 1);
            Assert.That(zeroStack, Is.GreaterThanOrEqualTo(0));
            clients[zeroStack].Dispose(); Until(() => clients[0].Latest.paused);
            Assert.That(Command(0, new HoldemWireRequest { type = "settle", handId = pending.handId,
                version = pending.version, oddChipRule = 1 }).error, Is.EqualTo("Paused"));
            Assert.That(clients[0].Latest.game.hasResult, Is.False, "Zero chips before odd-chip settlement are not elimination.");
            Connect(zeroStack); Until(() => clients.All(c => !c.Latest.paused));
            var settle = new HoldemWireRequest { type = "settle", handId = pending.handId,
                version = pending.version, oddChipRule = 1 };
            Assert.That(Command(2, settle).error, Is.EqualTo("HostOnly"));
            settle.id = Guid.NewGuid().ToString("N");
            var resolved = Command(0, settle); Assert.That(resolved.accepted, Is.True);
            Until(() => clients.All(c => c.Latest.game.version == resolved.receipt.version));
            string result = JsonUtility.ToJson(clients[0].Latest.game.result);
            for (int viewer = 0; viewer < 4; viewer++)
            {
                var game = clients[viewer].Latest.game;
                Assert.That(JsonUtility.ToJson(game.result), Is.EqualTo(result));
                Assert.That(game.result.pots.Length, Is.GreaterThan(1));
                Assert.That(game.result.pots.SelectMany(p => p.payouts).Any(p => p.includesOddChip), Is.True);
                Assert.That(game.seats.Sum(s => s.stack), Is.EqualTo(400));
                Assert.That(game.seats[3].visibleCards.Length, Is.EqualTo(viewer == 3 ? 2 : 0));
            }
            Assert.That(Command(0, settle).receipt.version, Is.EqualTo(resolved.receipt.version));
        }

        [Test]
        public void ReconnectingHostKeepsHostControlsButAnOldHandCommandDoesNotCrossToNextHand()
        {
            ConnectAll(); Start(); var old = clients[0].Latest.game;
            for (int i = 0; i < 3; i++) ActCurrent(true);
            clients[0].Dispose(); clients[0] = null;
            Until(() => clients[1].Latest.paused);
            Connect(0); Until(() => clients.All(c => !c.Latest.paused));
            Assert.That(clients[0].Latest.members.Single(m => m.seat == clients[0].Latest.viewerSeat).host, Is.True);
            Start();
            var wrong = Command(0, new HoldemWireRequest { type = "act", handId = old.handId,
                version = clients[0].Latest.game.version, action = 0 });
            Assert.That(wrong.receipt.error, Is.EqualTo("WrongHand"));
        }

        private sealed class StableRandom : IRandomSource { public int NextInt(int exclusiveMax) => exclusiveMax - 1; }
        private sealed class SeededRandom : IRandomSource
        {
            private readonly System.Random random;
            public SeededRandom(int seed) { random = new System.Random(seed); }
            public int NextInt(int exclusiveMax) => random.Next(exclusiveMax);
        }
    }
}
