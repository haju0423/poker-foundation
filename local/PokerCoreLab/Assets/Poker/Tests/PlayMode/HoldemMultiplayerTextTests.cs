using System.Collections;
using System.Linq;
using NUnit.Framework;
using Poker.Application;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator InvalidPlayerTextCanBeCorrectedWithoutLosingTheRoomOrBlockingBetting()
        {
            yield return Cleanup(); yield return SetupLobby(true);
            settings.utteranceVisibility = HoldemUtteranceVisibility.PublicRaw;
            Fields(0, 0, "방장\u2028다른 줄");
            yield return Click(0, "omc-room-host");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("이름"));
            Fields(0, 0, "주하🃏"); yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);

            Fields(1, boots[0].Connection.Port, "친구\u2029다른 줄");
            yield return Click(1, "omc-room-join");
            Assert.That(boots[1].Connection.HasSession, Is.False);
            Assert.That(boots[0].Connection.Remote.Lobby.MemberCount, Is.EqualTo(1));
            for (int i = 1; i < 4; i++)
            {
                Fields(i, boots[0].Connection.Port, "친구 " + i);
                yield return Click(i, "omc-room-join");
                int viewer = i; yield return Wait(() => boots[viewer].Connection.Remote?.Lobby != null);
            }
            for (int i = 0; i < 4; i++) yield return Click(i, "omc-room-ready");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Lobby.AllReady && !b.Connection.Remote.HasPendingInput));
            yield return Click(0, "omc-room-start");
            yield return Wait(() => roots.All(r => r.Q<Button>("omc-passive") != null));
            var field = roots[1].Q<TextField>("omc-utterance-input");
            long version = boots[1].Connection.Remote.Read().SessionVersion;
            int remaining = boots[1].Connection.Remote.Utterances.ReadUtterances().Remaining;
            foreach (char separator in new[] { '\u2028', '\u2029' })
            {
                field.value = "멘트" + separator + "다른 줄";
                yield return Click(1, "omc-utterance-send");
                yield return Wait(() => roots[1].Q<Label>("omc-utterance-status").text.Contains("줄바꿈"));
                Assert.That(boots[1].Connection.Remote.HasPendingUtterance, Is.False);
                Assert.That(boots[1].Connection.Remote.Utterances.ReadUtterances().Remaining, Is.EqualTo(remaining));
                Assert.That(boots.All(b => b.Connection.Remote.ReadPublicUtterances().Count == 0), Is.True);
                Assert.That(field.isReadOnly, Is.False);
            }
            const string text = "  ♥️ 사랑 🃏 👩‍💻  ";
            field.value = text; yield return Click(1, "omc-utterance-send");
            yield return Wait(() => boots.All(b => b.Connection.Remote.ReadPublicUtterances().Count == 1));
            Assert.That(boots.All(b => b.Connection.Remote.ReadPublicUtterances().GetEntry(0).Text == text), Is.True);
            Assert.That(boots[1].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
            yield return Click(3, "omc-passive");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion > version));
            Assert.That(boots.All(b => !b.HasFailed), Is.True);
        }
    }
}
