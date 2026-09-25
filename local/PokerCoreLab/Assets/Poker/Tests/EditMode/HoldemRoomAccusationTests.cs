using System;
using NUnit.Framework;
using Poker.Application;

namespace Poker.Foundation.Tests
{
    public sealed partial class HoldemRoomTests
    {
        private static HoldemConfig AccusationConfig(HoldemDealPolicy deal = HoldemDealPolicy.WaitForHost)
            => new HoldemConfig(100, 1, 2, HoldemRevealPolicy.PauseAfterCommunityReveal,
                HoldemAccusationMode.CollectLatestChoiceUntilHostCloses, deal);

        private static HoldemUtterancePolicy AccusationSpeech()
            => new HoldemUtterancePolicy(64, 1, HoldemUtteranceSeats.Active, HoldemUtteranceVisibility.PublicRaw);

        [Test]
        public void AccusationRoomsRequireExplicitEvidencePublicSpeechAndHostDealing()
        {
            HoldemRoom Build(HoldemConfig config, HoldemUtterancePolicy speech, HoldemAccusationEvidenceScope? scope)
                => new HoldemRoom(Guid.NewGuid(), Guid.NewGuid(), "방장", config, new SeatId(1), new StableRandom(),
                    utterancePolicy: speech, accusationEvidenceScope: scope);
            Assert.Throws<ArgumentException>(() => Build(AccusationConfig(), AccusationSpeech(), null));
            Assert.Throws<ArgumentException>(() => Build(AccusationConfig(), null, HoldemAccusationEvidenceScope.CurrentRevealOnly));
            Assert.Throws<ArgumentException>(() => Build(AccusationConfig(), new HoldemUtterancePolicy(64, 1, HoldemUtteranceSeats.Active),
                HoldemAccusationEvidenceScope.CurrentRevealOnly));
            Assert.Throws<ArgumentException>(() => Build(AccusationConfig(HoldemDealPolicy.Automatic), AccusationSpeech(),
                HoldemAccusationEvidenceScope.CurrentRevealOnly));
            Assert.Throws<ArgumentException>(() => Build(new HoldemConfig(100, 1, 2), AccusationSpeech(),
                HoldemAccusationEvidenceScope.CurrentRevealOnly));
            Assert.Throws<ArgumentException>(() => Build(AccusationConfig(), AccusationSpeech(), (HoldemAccusationEvidenceScope)99));
            Assert.DoesNotThrow(() => Build(AccusationConfig(), AccusationSpeech(), HoldemAccusationEvidenceScope.CurrentRevealOnly));
        }

        [Test]
        public void LegacyAccusationAdmissionCannotReserveOrRebindASeat()
        {
            Guid host = Guid.NewGuid(), guest = Guid.NewGuid();
            var preview = HoldemRoom.Create(Guid.NewGuid(), host, "방장", AccusationConfig(), new SeatId(1), new StableRandom(),
                out var hostAdmission, utterancePolicy: AccusationSpeech(), accusationEvidenceScope: HoldemAccusationEvidenceScope.CurrentRevealOnly);
            long revision = preview.Read(host).Revision;
            Assert.That(preview.Join(guest, "참가자").Error, Is.EqualTo(HoldemRoomError.ClientUpgradeRequired));
            Assert.That(preview.Read(host).MemberCount, Is.EqualTo(1));
            Assert.That(preview.Read(host).Revision, Is.EqualTo(revision));
            var joined = preview.Join(guest, "참가자", supportsAccusations: true);
            Assert.That(joined.Accepted, Is.True); Assert.That(joined.Admission.Seat.Value, Is.EqualTo(2));
            preview.Disconnect(guest); var replacement = Guid.NewGuid(); revision = preview.Read(host).Revision;
            Assert.That(preview.Reconnect(replacement, joined.Admission.ResumeToken).Error, Is.EqualTo(HoldemRoomError.ClientUpgradeRequired));
            Assert.That(preview.Read(host).Revision, Is.EqualTo(revision));
            Assert.That(preview.Reconnect(replacement, joined.Admission.ResumeToken, supportsAccusations: true).Accepted, Is.True);
            preview.Disconnect(host);
            Assert.That(preview.Reconnect(Guid.NewGuid(), hostAdmission.ResumeToken, supportsAccusations: true).Accepted, Is.True);
        }

        [Test]
        public void AccusationAuthorityChecksHostConnectionBeforeReadingOrClosingClaims()
        {
            room = new HoldemRoom(Guid.NewGuid(), peers[0], "방장", AccusationConfig(), new SeatId(1), new StableRandom(),
                utterancePolicy: AccusationSpeech(), accusationEvidenceScope: HoldemAccusationEvidenceScope.CurrentRevealOnly);
            for (int i = 1; i < 4; i++) admissions[i] = room.Join(peers[i], "참가자 " + i, supportsAccusations: true).Admission;
            foreach (var peer in peers) room.SetReady(peer, true);
            Assert.That(room.StartHand(peers[0], new HoldemStartCommand(room.Read(peers[0]).SessionId,
                Guid.NewGuid(), Guid.NewGuid(), 0)).Accepted, Is.True);
            while (room.Read(peers[0]).Game.CurrentSeat.HasValue)
            {
                int actor = room.Read(peers[0]).Game.CurrentSeat.Value.Value - 1;
                Assert.That(room.Submit(peers[actor], Passive(room.Read(peers[actor]).Game)).Accepted, Is.True);
            }
            var basis = room.Read(peers[0]).Game;
            Assert.That(room.DealUnchanged(peers[0], new HoldemDealCommand(basis.SessionId, basis.HandId,
                basis.PendingDeal.WindowId, Guid.NewGuid(), basis.SessionVersion, basis.PendingDeal.Street)).Accepted, Is.True);
            basis = room.Read(peers[0]).Game; long revision = room.Read(peers[0]).Revision;
            var close = new HoldemRoomAccusationClose(basis.SessionId, basis.HandId, basis.Accusations.WindowId,
                Guid.NewGuid(), basis.SessionVersion, basis.Street);
            Assert.That(room.CloseAccusations(peers[1], close).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.ResolveAccusations(peers[1]).Error, Is.EqualTo(HoldemRoomError.HostOnly));
            Assert.That(room.CloseAccusations(Guid.NewGuid(), close).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
            Assert.That(room.ResolveAccusations(Guid.NewGuid()).Error, Is.EqualTo(HoldemRoomError.UnknownConnection));
            Assert.That(room.Read(peers[0]).Revision, Is.EqualTo(revision));
            room.Disconnect(peers[1]);
            Assert.That(room.ResolveAccusations(peers[0]).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.CloseAccusations(peers[0], close).Error, Is.EqualTo(HoldemRoomError.Paused));
            Assert.That(room.ResolveAccusations(peers[1]).Error, Is.EqualTo(HoldemRoomError.Disconnected));
        }
    }
}
