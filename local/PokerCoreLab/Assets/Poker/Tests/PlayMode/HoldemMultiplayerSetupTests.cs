using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Poker.Foundation;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Poker.Runtime.Tests
{
    public sealed partial class HoldemMultiplayerLobbyTests
    {
        private void ChipFields(int viewer, string stack, string small, string big)
        {
            roots[viewer].Q<TextField>("omc-room-stack").value = stack;
            roots[viewer].Q<TextField>("omc-room-small-blind").value = small;
            roots[viewer].Q<TextField>("omc-room-big-blind").value = big;
        }

        [UnityTest]
        public IEnumerator HostChipSetupIsAuthoritativeAndDoesNotMutateTheSettingsAsset()
        {
            Assert.That(roots[0].Q<Foldout>("omc-room-setup").value, Is.False);
            ChipFields(0, "250", "5", "10");
            // Joining uses the host's rules even if these unused local hosting fields are invalid.
            for (int i = 1; i < 4; i++) ChipFields(i, "invalid", "-1", "0");
            yield return StartGame();
            for (int i = 0; i < 4; i++)
            {
                var rules = boots[i].Connection.Remote.Lobby.Rules;
                Assert.That(rules.StartingStack, Is.EqualTo(250));
                Assert.That(rules.SmallBlind, Is.EqualTo(5));
                Assert.That(rules.BigBlind, Is.EqualTo(10));
                Assert.That(roots[i].Q<Foldout>("omc-room-setup").enabledInHierarchy, Is.False);
                var packet = Client(i).Latest.game;
                Assert.That(packet.seats.Sum(s => s.stack) + packet.pot, Is.EqualTo(1000));
            }
            Assert.That(settings.startingStack, Is.EqualTo(100));
            Assert.That(settings.smallBlind, Is.EqualTo(1));
            Assert.That(settings.bigBlind, Is.EqualTo(2));
            // A programmatic UI edit after hosting must not replace the captured room configuration.
            ChipFields(0, "700", "20", "40");
            yield return null;
            Assert.That(boots[0].Connection.Remote.Lobby.Rules.StartingStack, Is.EqualTo(250));
        }

        [UnityTest]
        public IEnumerator InvalidChipSetupNeverStartsAConnectionAndCanBeCorrected()
        {
            Fields(0, 0);
            string[] invalid = { "", "0", "-1", "1.5", "abc", "1,00", "9223372036854775808",
                "9,223,372,036,854,775,8070" };
            foreach (var value in invalid)
            {
                for (int field = 0; field < 3; field++)
                {
                    ChipFields(0, field == 0 ? value : "100", field == 1 ? value : "1", field == 2 ? value : "2");
                    var input = roots[0].Q<TextField>(new[] { "omc-room-stack", "omc-room-small-blind", "omc-room-big-blind" }[field]);
                    Assert.That(input.maxLength, Is.EqualTo(64));
                    Assert.That(input.value, Is.EqualTo(value));
                    yield return Click(0, "omc-room-host");
                    Assert.That(boots[0].Connection.HasSession, Is.False, value + " / " + field);
                    Assert.That(boots[0].Connection.IsHosting, Is.False);
                    Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("정수"));
                }
            }
            ChipFields(0, (long.MaxValue / 4 + 1).ToString(CultureInfo.InvariantCulture), "1", "2");
            yield return Click(0, "omc-room-host");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("너무 커요"));
            ChipFields(0, "100", "10", "5");
            yield return Click(0, "omc-room-host");
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Contain("스몰 블라인드 이상"));
            ChipFields(0, " 2,500 ", "100", "1,000");
            yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].HasFailed, Is.False);
            Assert.That(boots[0].Connection.Remote.Lobby.Rules.StartingStack, Is.EqualTo(2500));
            Assert.That(boots[0].Connection.Remote.Lobby.Rules.BigBlind, Is.EqualTo(1000));
            Assert.That(roots[0].Q<Label>("omc-room-status").text, Does.Not.Contain("입력해"));
        }

        [UnityTest]
        public IEnumerator ShortStartingStacksRemainLegalAndHostingSettingsSurviveReturnHome()
        {
            ChipFields(0, "1", "1", "2");
            yield return StartGame();
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack) + Client(0).Latest.game.pot, Is.EqualTo(4));
            Assert.That(boots[0].HasFailed, Is.False);
            yield return Click(0, "omc-room-leave");
            yield return Click(0, "omc-room-confirm-leave");
            yield return Wait(() => !boots[0].Connection.HasSession);
            Assert.That(roots[0].Q<TextField>("omc-room-stack").value, Is.EqualTo("1"));
            Assert.That(roots[0].Q<Foldout>("omc-room-setup").enabledInHierarchy, Is.True);
            yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].Connection.Remote.Lobby.Rules.StartingStack, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExpandedChipSetupFitsSmallWindowAndKeepsHostButtonReachable()
        {
            var old = textures[0]; textures[0] = new RenderTexture(960, 640, 0);
            textures[0].Create(); panels[0].targetTexture = textures[0]; old.Release(); UnityEngine.Object.Destroy(old);
            roots[0].Q<Foldout>("omc-room-setup").value = true;
            yield return null; yield return null; yield return null;
            Capture(0, "chip-setup-small");
            var capacity = roots[0].Q<DropdownField>("omc-room-capacity");
            Assert.That(capacity.worldBound.width, Is.GreaterThan(200));
            Assert.That(capacity.worldBound.xMax, Is.LessThanOrEqualTo(roots[0].worldBound.xMax));
            Assert.That(capacity.worldBound.yMax, Is.LessThanOrEqualTo(roots[0].Q<TextField>("omc-room-stack").worldBound.yMin));
            var fields = new[] { "omc-room-stack", "omc-room-small-blind", "omc-room-big-blind" }
                .Select(name => roots[0].Q<TextField>(name)).ToArray();
            for (int i = 0; i < fields.Length; i++)
            {
                Assert.That(fields[i].worldBound.width, Is.GreaterThan(200));
                Assert.That(fields[i].worldBound.xMax, Is.LessThanOrEqualTo(roots[0].worldBound.xMax));
                if (i > 0) Assert.That(fields[i].worldBound.yMin, Is.GreaterThanOrEqualTo(fields[i - 1].worldBound.yMax));
            }
            ChipFields(0, "250", "5", "10"); Fields(0, 0);
            roots[0].Q<ScrollView>("omc-lobby").ScrollTo(roots[0].Q<Button>("omc-room-host"));
            yield return null; yield return null;
            Assert.That(Pickable(0, "omc-room-host"), Is.True);
            yield return Click(0, "omc-room-host");
            yield return Wait(() => boots[0].Connection.Remote?.Lobby != null);
            Assert.That(boots[0].Connection.Remote.Lobby.Rules.StartingStack, Is.EqualTo(250));
        }

        [UnityTest]
        public IEnumerator ChipSetupValidatesFourSeatCapacityWithoutChangingFlowPolicies()
        {
            foreach (bool flow in new[] { false, true })
            {
                settings.enableFlowPreview = flow;
                var config = settings.CreateConfig(long.MaxValue / 4, 1, long.MaxValue);
                Assert.That(config.StartingStack, Is.EqualTo(long.MaxValue / 4));
                Assert.That(config.DealPolicy, Is.EqualTo(flow ? HoldemDealPolicy.WaitForHost : HoldemDealPolicy.Automatic));
                Assert.That(config.RevealPolicy, Is.EqualTo(flow ? HoldemRevealPolicy.PauseAfterCommunityReveal : HoldemRevealPolicy.Automatic));
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.CreateConfig(long.MaxValue / 4 + 1, 1, 2));
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.CreateConfig(0, 1, 2));
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.CreateConfig(100, 0, 2));
                Assert.Throws<ArgumentOutOfRangeException>(() => settings.CreateConfig(100, 3, 2));
            }
            Assert.That(boots[0].Connection.Host("방장", "127.0.0.1", 0, false,
                new HoldemConfig(long.MaxValue / 4 + 1, 1, 2)), Is.False);
            Assert.That(boots[0].Connection.HasSession, Is.False);
            Assert.That(boots[0].Connection.IsHosting, Is.False);
            Assert.That(boots[0].Connection.ErrorText, Does.Contain("시작 칩 합계"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator LargestFourSeatStartingStackCanCreateAndStartARoom()
        {
            long maximum = long.MaxValue / 4;
            for (int i = 0; i < 4; i++)
            {
                var old = textures[i]; textures[i] = new RenderTexture(960, 640, 0);
                textures[i].Create(); panels[i].targetTexture = textures[i]; old.Release(); UnityEngine.Object.Destroy(old);
            }
            string formattedMaximum = maximum.ToString("N0", CultureInfo.InvariantCulture);
            ChipFields(0, formattedMaximum, "1", "2");
            Assert.That(roots[0].Q<TextField>("omc-room-stack").value, Is.EqualTo(formattedMaximum));
            yield return StartGame();
            for (int i = 0; i < 4; i++)
            {
                Assert.That(boots[i].HasFailed, Is.False);
                var game = Client(i).Latest.game;
                Assert.That(game.seats.Sum(s => s.stack) + game.pot, Is.EqualTo(maximum * 4));
                Assert.That(boots[i].Connection.Remote.Lobby.Rules.StartingStack, Is.EqualTo(maximum));
            }
            yield return null; yield return null;
            Capture(3, "large-chips-small");
            yield return Click(3, "omc-max"); yield return null;
            Capture(3, "large-chips-all-in");
            AssertLargeChipLayout(3);
            string formattedTarget = (maximum - 10).ToString("N0", CultureInfo.InvariantCulture);
            roots[3].Q<TextField>("omc-target").value = formattedTarget;
            Assert.That(roots[3].Q<TextField>("omc-target").value, Is.EqualTo(formattedTarget));
            Assert.That(roots[3].Q<Button>("omc-aggressive").enabledInHierarchy, Is.True);
            yield return Click(3, "omc-aggressive");
            yield return Wait(() => Client(0).Latest.game.currentSeat == 1);
            Assert.That(boots[0].Connection.Remote.Read().GetSeat(new SeatId(4)).Stack, Is.EqualTo(10));
            yield return Click(0, "omc-max"); yield return null; yield return null;
            Capture(0, "large-chips-call-raise");
            AssertLargeChipLayout(0);
            long version = boots[0].Connection.Remote.Read().SessionVersion;
            yield return Click(0, "omc-aggressive");
            yield return Wait(() => boots[0].Connection.Remote.Read().SessionVersion > version);
            for (int guard = 0; guard < 12 && boots[0].Connection.Remote.Read().Result == null; guard++)
            {
                yield return Wait(() => boots[0].Connection.Remote.Read().Result != null
                    || boots[0].Connection.Remote.Read().IsSettlementPending
                    || Client(0).Latest.game.currentSeat > 0);
                if (boots[0].Connection.Remote.Read().Result != null) break;
                version = boots[0].Connection.Remote.Read().SessionVersion;
                if (boots[0].Connection.Remote.Read().IsSettlementPending) yield return Click(0, "omc-resolve");
                else yield return Click(Client(0).Latest.game.currentSeat - 1, "omc-passive");
                yield return Wait(() => boots[0].Connection.Remote.Read().SessionVersion > version);
            }
            yield return Wait(() => boots.All(b => b.Connection.Remote.Read().Result != null));
            yield return null; yield return null;
            for (int i = 0; i < 4; i++) AssertLargeChipLayout(i);
            Capture(0, "large-chips-settled");
            Assert.That(Client(0).Latest.game.seats.Sum(s => s.stack), Is.EqualTo(maximum * 4));
        }

        private void AssertLargeChipLayout(int viewer)
        {
            var root = roots[viewer];
            foreach (var info in root.Query(className: "omc-seat-info").ToList())
            {
                var stack = info.Q<Label>(className: "omc-stack");
                Assert.That(stack.tooltip, Is.EqualTo(stack.text));
                Assert.That(stack.isElided, Is.False);
                var measured = stack.MeasureTextSize(stack.text, stack.contentRect.width,
                    VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined);
                Assert.That(measured.y, Is.LessThanOrEqualTo(stack.contentRect.height + 1), "Full chip count must be visible.");
                Assert.That(info.worldBound.xMax, Is.LessThanOrEqualTo(info.parent.Q(className: "omc-hand-area").worldBound.xMin));
                var status = info.Q<Label>(className: "omc-seat-status");
                Assert.That(status.tooltip, Is.EqualTo(status.text));
                var statusSize = status.MeasureTextSize(status.text, status.contentRect.width,
                    VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined);
                Assert.That(statusSize.y, Is.LessThanOrEqualTo(status.contentRect.height + 1));
            }
            foreach (string name in new[] { "omc-fold", "omc-passive", "omc-aggressive", "omc-amount-hint" })
            {
                var item = root.Q(name);
                if (item.resolvedStyle.display == DisplayStyle.None || item.worldBound.width <= 0) continue;
                Assert.That(item.worldBound.xMin, Is.GreaterThanOrEqualTo(root.worldBound.xMin), name);
                Assert.That(item.worldBound.xMax, Is.LessThanOrEqualTo(root.worldBound.xMax), name);
                if (item is TextElement text)
                {
                    var measured = text.MeasureTextSize(text.text, text.contentRect.width,
                        VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined);
                    Assert.That(measured.y, Is.LessThanOrEqualTo(text.contentRect.height + 1),
                        "Full action amount must be visible: " + name);
                    Assert.That(text.isElided, Is.False, name);
                    if (name != "omc-fold") Assert.That(text.tooltip, Is.EqualTo(text.text));
                }
            }
            Assert.That(root.Q("omc-own-cards").worldBound.yMax,
                Is.LessThanOrEqualTo(root.Q<Label>(className: "omc-prompt").worldBound.yMin), "Cards must not overlap the action prompt.");
        }
    }
}
