using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator CopiedRoomAddressJoinsWithoutChangingClipboardOrAutoConnecting()
        {
            Fields(0, 0);
            yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            var share = roots[0].Q<TextField>("omc-room-share-address");
            Assert.That(share, Is.Not.Null, "The admitted room needs a selectable share address.");
            Assert.That(share.isReadOnly, Is.True);
            string copied = null;
            InjectAddressCopy(0, value => copied = value);
            Assert.That(copied, Is.Null);
            yield return Click(0, "omc-room-copy-address");
            Assert.That(copied, Is.EqualTo(boots[0].Connection.EndpointText));
            Assert.That(copied, Does.StartWith("127.0.0.1:"));
            Assert.That(roots[0].Q<Label>("omc-room-share-hint").text, Does.Contain("같은 컴퓨터"));
            yield return Click(0, "omc-room-leave");
            Assert.That(roots[0].Q<Button>("omc-room-copy-address").enabledInHierarchy, Is.False);
            yield return Click(0, "omc-room-stay");
            for (int i = 1; i < 4; i++)
            {
                Fields(i, 1);
                roots[i].Q<TextField>("omc-room-address").value = "  " + copied + "  ";
                // The explicit port in a shared address takes precedence over the separate field.
                roots[i].Q<TextField>("omc-room-port").value = "bad";
                yield return null;
                Assert.That(boots[i].Connection.HasSession, Is.False);
                yield return Click(i, "omc-room-join");
                int viewer = i;
                yield return Wait(() => boots[viewer].Connection.Remote?.Lobby != null);
                Assert.That(boots[i].Connection.EndpointText, Is.EqualTo(copied));
                Assert.That(roots[i].Q<Toggle>("omc-room-lan").value, Is.False);
            }
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.MemberCount == 4));
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => boots.All(b => b.Connection.Remote.HasGame));
            long version = boots[0].Connection.Remote.Read().SessionVersion;
            int actor = boots[0].Connection.Remote.Read().CurrentSeat.Value.Value - 1;
            yield return Click(actor, "omc-passive");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion == version + 1));
            Assert.That(roots[0].Q<VisualElement>("omc-room-share").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
        }

        [UnityTest]
        public IEnumerator InvalidSharedAddressDoesNotReserveASeatOrSelectLan()
        {
            Fields(0, 7777);
            var input = roots[0].Q<TextField>("omc-room-address");
            foreach (string invalid in new[] { "127.0.0.1:0", "127.0.0.1:65536", "127.0.0.1:7777:8", "127.0.0.1:7777/path" })
            {
                input.value = invalid;
                yield return Click(0, "omc-room-join");
                Assert.That(boots[0].Connection.HasSession, Is.False, invalid);
                Assert.That(input.value, Is.EqualTo(invalid), "Invalid input should stay available for correction.");
                Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("방 주소"));
            }
            input.value = "192.168.10.20:7777";
            yield return Click(0, "omc-room-join");
            Assert.That(roots[0].Q<Toggle>("omc-room-lan").value, Is.False);
            Assert.That(boots[0].Connection.HasSession, Is.False, "Pasting must not enable private network access.");
            Assert.That(boots[0].Connection.ErrorText, Does.Contain("선택해 주세요"));
            Assert.That(input.value, Is.EqualTo("192.168.10.20:7777"));
            input.value = "8.8.8.8:7777";
            roots[0].Q<Toggle>("omc-room-lan").value = true;
            yield return Click(0, "omc-room-join");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(boots[0].Connection.ErrorText, Does.Contain("사설 IP"));
            Assert.That(input.value, Is.EqualTo("8.8.8.8:7777"));
        }

        [UnityTest]
        public IEnumerator SharedAddressCannotAccidentallyCreateADifferentRoom()
        {
            Fields(0, 0);
            var input = roots[0].Q<TextField>("omc-room-address");
            input.value = "127.0.0.1:7777";
            yield return Click(0, "omc-room-host");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("참가"));
            Assert.That(input.value, Is.EqualTo("127.0.0.1:7777"));
            Assert.That(roots[0].Q<TextField>("omc-room-port").value, Is.EqualTo("0"));
            input.value = "127.0.0.1";
            yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].Connection.IsHosting, Is.True);
        }

        [Test]
        public void NetworkPermissionErrorsAreSpecificAndNeverReserveASeat()
        {
            using (var connection = new HoldemMultiplayerConnection())
            {
                foreach (bool hosting in new[] { false, true })
                {
                    bool Start(string ip, bool lan) => hosting
                        ? connection.Host("친구", ip, 7777, lan, settings.CreateConfig())
                        : connection.Join("친구", ip, 7777, lan);
                    Assert.That(Start("192.168.10.20", false), Is.False);
                    Assert.That(connection.ErrorText, Does.Contain("선택해 주세요"));
                    Assert.That(Start("8.8.8.8", true), Is.False);
                    Assert.That(connection.ErrorText, Does.Contain("사설 IP"));
                    Assert.That(Start("not-an-address", true), Is.False);
                    Assert.That(connection.ErrorText, Does.Contain("IP 주소"));
                    Assert.That(connection.HasSession, Is.False);
                    Assert.That(connection.IsConnecting, Is.False);
                    Assert.That(connection.IsHosting, Is.False);
                }
            }
        }

        [UnityTest]
        public IEnumerator ShareAddressIsReadableOnSmallWindowAndCopyFailureKeepsRoom()
        {
            for (int i = 0; i < 4; i++)
            {
                textures[i].Release(); textures[i].width = 960; textures[i].height = 640; textures[i].Create();
            }
            yield return EnterRoom();
            var original = boots[0].Connection.Remote;
            string expected = boots[0].Connection.EndpointText;
            InjectAddressCopy(0, _ => throw new InvalidOperationException("Simulated clipboard failure"));
            yield return Click(0, "omc-room-copy-address");
            Assert.That(boots[0].HasFailed, Is.False);
            Assert.That(boots[0].Connection.Remote, Is.SameAs(original));
            Assert.That(roots[0].Q<TextField>("omc-room-share-address").value, Is.EqualTo(expected));
            Assert.That(roots[0].Q<Label>("omc-room-share-hint").text, Does.Contain("직접"));
            Assert.That(Pickable(0, "omc-room-ready"), Is.True);
            yield return null; yield return null;
            var displayedText = roots[0].Q<TextField>("omc-room-share-address")
                .Query<TextElement>().ToList().First(element => element.text == expected);
            Assert.That(displayedText.resolvedStyle.color.r, Is.LessThan(0.25f), "Address needs dark text on its light input background.");
            Capture(0, "address-small");
            yield return Click(0, "omc-room-leave");
            yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => !boots[0].Connection.HasSession);
            Assert.That(roots[0].Q<TextField>("omc-room-share-address").value, Is.Empty);
            Assert.That(roots[0].Q<VisualElement>("omc-room-share").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Fields(0, 0);
            yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(roots[0].Q<Label>("omc-room-share-hint").text, Does.Not.Contain("복사"));
        }

        private void InjectAddressCopy(int viewer, Action<string> writer)
        {
            var field = typeof(HoldemMultiplayerBootstrap).GetField("copyAddress", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(boots[viewer], writer);
        }
    }
}
