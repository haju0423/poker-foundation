using System.Collections;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        [UnityTest]
        public IEnumerator RoomInfoShowsHostSettingsWithoutPausingOtherPlayers()
        {
            ChipFields(0, "250", "5", "10");
            for (int i = 1; i < 4; i++) ChipFields(i, "700", "20", "40");
            ResizeRoomViewer(0);
            yield return StartGame();
            long version = boots[0].Connection.Remote.Read().SessionVersion;
            for (int i = 0; i < 4; i++)
            {
                yield return Click(i, "omc-help"); yield return Click(i, "omc-help-page-3");
                string text = roots[i].Q<Label>("omc-help-copy").text;
                Assert.That(text, Does.StartWith("방 인원: 4인\n시작 칩: 250칩\n스몰 블라인드: 5칩\n빅 블라인드: 10칩"));
                Assert.That(text, Does.Contain("공용 카드: 자동 공개").And.Contain("멘트: 사용 안 함"));
                Assert.That(text, Does.Not.Contain("700칩"));
                Assert.That(roots[i].Q<Button>("omc-help-page-3").ClassListContains("omc-primary"), Is.True);
                Assert.That(Pickable(i, "omc-close-help"), Is.True);
                Assert.That(boots[i].Connection.Remote.Read().SessionVersion, Is.EqualTo(version));
            }
            yield return null; Capture(0, "room-info-small");
            // One player may read help while another player acts. Opening it is not a room pause.
            yield return Click(3, "omc-close-help"); yield return Click(3, "omc-passive");
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().SessionVersion == version + 1));
            Assert.That(roots[0].Q<Label>("omc-help-copy").text, Does.StartWith("방 인원: 4인\n시작 칩: 250칩"));
            Assert.That(Pickable(0, "omc-passive"), Is.False);
            yield return Click(0, "omc-close-help");
            yield return Wait(() => Pickable(0, "omc-passive"));
            yield return Click(0, "omc-passive");
            yield return Wait(() => boots[0].Connection.Remote.Read().SessionVersion == version + 2);
        }

        [UnityTest]
        public IEnumerator FlowRoomInfoKeepsFullAmountsReachableInSmallWindow()
        {
            yield return Cleanup(); yield return SetupLobby(true);
            ResizeRoomViewer(0);
            ChipFields(0, (long.MaxValue / 4).ToString(CultureInfo.InvariantCulture), "1", "2");
            yield return StartGame();
            yield return Click(0, "omc-help"); yield return Click(0, "omc-help-page-3");
            var copy = roots[0].Q<Label>("omc-help-copy");
            Assert.That(copy.text, Does.Contain("2,305,843,009,213,693,951칩"));
            Assert.That(copy.text, Does.Contain("공용 카드: 방장이 공개").And.Contain("확인 후 방장이 계속"));
            Assert.That(copy.text, Does.Contain("접수 확인용 · AI나 카드에는 반영되지 않아요."));
            var scroll = roots[0].Q<ScrollView>(className: "omc-help-scroll");
            // Exercise a real nonzero offset even if the current text fits the default viewport.
            scroll.style.maxHeight = 140;
            yield return null; yield return null;
            Assert.That(scroll.verticalScroller.highValue, Is.GreaterThan(0));
            scroll.scrollOffset = new Vector2(0, scroll.verticalScroller.highValue);
            yield return null; yield return null;
            Assert.That(copy.worldBound.yMax, Is.LessThanOrEqualTo(scroll.contentViewport.worldBound.yMax + 1));
            for (int i = 0; i < 4; i++) Assert.That(Pickable(0, "omc-help-page-" + i), Is.True);
            Assert.That(Pickable(0, "omc-close-help"), Is.True);
            Capture(0, "room-info-flow-scrolled");
            float offset = scroll.scrollOffset.y;
            long version = boots[0].Connection.Remote.Read().SessionVersion;
            yield return Click(3, "omc-passive");
            yield return Wait(() => boots[0].Connection.Remote.Read().SessionVersion == version + 1);
            yield return null;
            Assert.That(scroll.scrollOffset.y, Is.EqualTo(offset).Within(1), "Remote changes must not reset the reading position.");
            yield return Click(0, "omc-help-page-0");
            Assert.That(copy.text, Is.EqualTo(HoldemTableScreen.HelpText));
            Assert.That(scroll.scrollOffset.y, Is.Zero);
            yield return Click(0, "omc-close-help");
        }

        private void ResizeRoomViewer(int viewer)
        {
            var old = textures[viewer]; textures[viewer] = new RenderTexture(960, 640, 0);
            textures[viewer].Create(); panels[viewer].targetTexture = textures[viewer];
            old.Release(); UnityEngine.Object.Destroy(old);
        }
    }
}
