using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace Poker.Editor.Tests
{
    public sealed class HoldemBuildNoticesTests
    {
        private string scratch, assets, original, bundled;

        [SetUp]
        public void SetUp()
        {
            scratch = Path.Combine(Path.GetTempPath(), "omc-font-notice-" + Guid.NewGuid().ToString("N"));
            assets = Path.Combine(scratch, "Assets");
            original = Path.Combine(assets, "Poker/Resources/Fonts/OFL.txt");
            bundled = Path.Combine(assets, "StreamingAssets", HoldemBuildNotices.RelativeNoticePath);
            Write(original, "Fixture copyright\nFixture license\n");
            Write(bundled, File.ReadAllText(original));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }

        [Test]
        public void ProjectShipsTheUnmodifiedOriginalNotice()
            => Assert.DoesNotThrow(() => HoldemBuildNotices.ValidateSource(UnityEngine.Application.dataPath));

        [TestCase("original")]
        [TestCase("bundled")]
        [TestCase("empty")]
        [TestCase("modified")]
        public void InvalidSourceNoticeStopsTheBuild(string defect)
        {
            if (defect == "original") File.Delete(original);
            else if (defect == "bundled") File.Delete(bundled);
            else if (defect == "empty") { Write(original, ""); Write(bundled, ""); }
            else Write(bundled, "Changed notice");
            Assert.Throws<InvalidOperationException>(() => HoldemBuildNotices.ValidateSource(assets));
        }

        [TestCase(BuildTarget.StandaloneOSX)]
        [TestCase(BuildTarget.StandaloneWindows64)]
        public void DesktopPlayerContainsTheExactNotice(BuildTarget target)
        {
            var output = Player(target, out var notice);
            Write(notice, File.ReadAllText(original));
            Assert.DoesNotThrow(() => HoldemBuildNotices.ValidatePlayer(assets, target, output));
        }

        [TestCase(BuildTarget.StandaloneOSX)]
        [TestCase(BuildTarget.StandaloneWindows64)]
        public void AdjacentNoticeIsNotEnoughWhenPlayerNoticeIsMissing(BuildTarget target)
        {
            var output = Player(target, out _);
            Write(Path.Combine(Path.GetDirectoryName(output), "FONT-LICENSE.txt"), File.ReadAllText(original));
            Assert.Throws<InvalidOperationException>(() => HoldemBuildNotices.ValidatePlayer(assets, target, output));
        }

        [TestCase(BuildTarget.StandaloneOSX)]
        [TestCase(BuildTarget.StandaloneWindows64)]
        public void ModifiedPlayerNoticeIsRejected(BuildTarget target)
        {
            var output = Player(target, out var notice);
            Write(notice, "Changed notice");
            Assert.Throws<InvalidOperationException>(() => HoldemBuildNotices.ValidatePlayer(assets, target, output));
        }

        private string Player(BuildTarget target, out string notice)
        {
            string root = Path.Combine(scratch, "Build");
            string output = Path.Combine(root, target == BuildTarget.StandaloneOSX ? "Table.app" : "Table.exe");
            string data = target == BuildTarget.StandaloneOSX
                ? Path.Combine(output, "Contents/Resources/Data") : Path.Combine(root, "Table_Data");
            notice = Path.Combine(data, "StreamingAssets", HoldemBuildNotices.RelativeNoticePath);
            return output;
        }

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }
    }
}
