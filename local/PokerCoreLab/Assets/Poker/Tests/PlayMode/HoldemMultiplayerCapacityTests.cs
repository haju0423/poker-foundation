using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using Poker.Foundation;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        private IEnumerator EnterThreeSeatRoom()
        {
            Assert.That(roots[0].Q<DropdownField>("omc-room-capacity").value, Is.EqualTo("4인"));
            roots[0].Q<DropdownField>("omc-room-capacity").index = 0;
            Fields(0, 0); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            for (int i = 1; i < 3; i++)
            {
                Assert.That(roots[i].Q<DropdownField>("omc-room-capacity").value, Is.EqualTo("4인"));
                Fields(i, boots[0].Connection.Port); yield return Click(i, "omc-room-join");
                int viewer = i; yield return Wait(() => boots[viewer].Connection.Remote?.Lobby != null);
            }
            yield return Wait(() => boots.Take(3).All(b => b.Connection.Remote.Lobby.MemberCount == 3));
        }
        [UnityTest]
        public IEnumerator ThreeSeatLobbyStartsPlaysSettlesAndReconnectsOverRealTcp()
        {
            yield return EnterThreeSeatRoom();
            for (int i = 0; i < 3; i++)
            {
                Assert.That(boots[i].Connection.Remote.Lobby.SeatCapacity, Is.EqualTo(3));
                Assert.That(roots[i].Q<DropdownField>("omc-room-capacity").enabledInHierarchy, Is.False);
            }
            Fields(3, boots[0].Connection.Port); yield return Click(3, "omc-room-join");
            yield return Wait(() => Client(3)?.AdmissionError != null);
            Assert.That(Client(3).AdmissionError, Is.EqualTo("Full"));
            Assert.That(boots[3].Connection.Remote?.Lobby, Is.Null);
            for (int i = 0; i < 3; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.Take(3).All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => roots.Take(3).All(r => r.Q<Button>("omc-passive") != null));
            for (int i = 0; i < 3; i++)
            {
                Assert.That(Client(i).Latest.game.seats.Length, Is.EqualTo(3));
                Assert.That(Client(i).Latest.game.seats.Count(s => s.visibleCards.Length != 0), Is.EqualTo(1));
                Assert.That(roots[i].Q<Label>(className: "omc-subtitle").text, Is.EqualTo("3인 멀티플레이"));
            }
            // Even a programmatic change cannot rewrite the captured room capacity.
            roots[0].Q<DropdownField>("omc-room-capacity").index = 1;
            int seat = Client(2).Identity.Seat; Client(2).Dispose();
            yield return Wait(() => boots[0].Connection.Remote.Lobby.Paused);
            yield return Click(2, "omc-room-reconnect");
            yield return Wait(() => boots.Take(3).All(b => !b.Connection.Remote.Lobby.Paused));
            Assert.That(Client(2).Identity.Seat, Is.EqualTo(seat));
            for (int guard = 0; guard < 40 && boots[0].Connection.Remote.Read().Result == null; guard++)
            {
                var game = boots[0].Connection.Remote.Read(); long version = game.SessionVersion;
                if (game.IsSettlementPending) yield return Click(0, "omc-resolve");
                else yield return Click(game.CurrentSeat.Value.Value - 1, "omc-passive");
                yield return Wait(() => boots.Take(3).All(b => b.Connection.Remote.Read().SessionVersion > version));
            }
            yield return Wait(() => boots.Take(3).All(b => b.Connection.Remote.Read().Result != null));
            for (int i = 0; i < 3; i++) Assert.That(Client(i).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(300));
            yield return Click(0, "omc-next");
            yield return Wait(() => boots.Take(3).All(b => b.Connection.Remote.Read().HandNumber == 2));
            Assert.That(boots.Take(3).All(b => b.Connection.Remote.Lobby.SeatCapacity == 3), Is.True);
            yield return null; yield return null;
            Assert.That(roots[0].Q<Label>(className: "omc-counter").text, Does.Contain("2번째 판"));
            Capture(0, "three-seat-next-hand");
        }
        [UnityTest]
        public IEnumerator RoomCapacityCannotChangeDisappearOrArriveAfterALegacyLobby()
        {
            foreach (string change in new[] { "change", "missing", "legacy" })
            {
                if (change != "change") { yield return Cleanup(); yield return SetupLobby(false); }
                yield return EnterThreeSeatRoom();
                var packet = Client(1).Latest;
                if (change == "legacy")
                {
                    packet.hasRules = false;
                    using (var reader = new HoldemRemoteTablePort(Client(1)))
                    {
                        reader.Poll(); Assert.That(reader.Lobby.SeatCapacity, Is.EqualTo(4));
                        packet.hasRules = true; packet.revision++; reader.Poll();
                        Assert.That(Client(1).IsClosed, Is.True);
                        Assert.That(reader.Lobby.SeatCapacity, Is.EqualTo(4));
                    }
                }
                else
                {
                    var reader = boots[1].Connection.Remote; var before = reader.Lobby;
                    if (change == "change") packet.rules.seatCapacity = 4;
                    else packet.hasRules = false;
                    packet.revision++; reader.Poll();
                    Assert.That(Client(1).IsClosed, Is.True);
                    Assert.That(reader.Lobby, Is.SameAs(before));
                    Assert.That(reader.CanSend, Is.False);
                }
            }
        }
        [UnityTest]
        public IEnumerator ThreeSeatSetupAcceptsItsOwnMaximumAndRejectsOverflow()
        {
            Fields(0, 0); roots[0].Q<DropdownField>("omc-room-capacity").index = 0;
            long maximum = long.MaxValue / 3;
            ChipFields(0, (maximum + 1).ToString(), "1", "2"); yield return Click(0, "omc-room-host");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            ChipFields(0, maximum.ToString(), "1", "2"); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].Connection.Remote.Lobby.Rules.StartingStack, Is.EqualTo(maximum));
            Assert.That(boots[0].Connection.Remote.Lobby.SeatCapacity, Is.EqualTo(3));
        }
    }
}
