using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Poker.Runtime;

namespace Poker.Foundation.Tests
{
    public sealed class PracticeBuildSmokeTests
    {
        [Test]
        public void ReportFieldsAreLimitedToVersionAndNonPrivateRunMetadata()
        {
            var fields = typeof(PracticeSmokeReport).GetFields(BindingFlags.Instance | BindingFlags.Public);
            Assert.That(fields.Select(field => field.Name), Is.EquivalentTo(new[]
            {
                "schemaVersion", "kind", "passed", "developmentBuild", "batchMode", "failureType", "unityVersion", "frames",
                "completedHands", "explicitRestarts", "humanActions", "exchangeHands", "secondBettingHands"
            }));
            foreach (var field in fields)
                Assert.That(field.FieldType == typeof(int) || field.FieldType == typeof(bool) || field.FieldType == typeof(string), Is.True);
        }
        [TestCase(false, false, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, true, true, true)]
        [TestCase(true, false, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, true, true, false)]
        public void OnlyExplicitHeadlessDevelopmentPlayerCanInstall(bool editor, bool batch, bool development, bool expected)
        {
            Assert.That(PracticeBuildSmoke.ShouldRun(new[] { "player", PracticeBuildSmoke.OutputArgument }, editor, batch, development), Is.EqualTo(expected));
            Assert.That(PracticeBuildSmoke.ShouldRun(new[] { "player" }, editor, batch, development), Is.False);
        }
        [Test]
        public void MissingOrLookalikeArgumentsDoNotOptIn()
        {
            Assert.That(PracticeBuildSmoke.ShouldRun(null, false, true, true), Is.False);
            foreach (string arg in new[] { "-poker-smoke-output=x", "--poker-smoke-output", "-POKER-SMOKE-OUTPUT", "poker-smoke-output" })
                Assert.That(PracticeBuildSmoke.ShouldRun(new[] { arg }, false, true, true), Is.False);
        }
        [Test]
        public void ParserAcceptsOnlyAnUnusedAbsoluteJsonFileAndDoesNotCreateIt()
        {
            string path = Path.Combine(Path.GetTempPath(), "poker-smoke-" + Guid.NewGuid().ToString("N") + ".json");
            Assert.That(PracticeBuildSmoke.ReadOutput(new[] { "player", "-nographics", PracticeBuildSmoke.OutputArgument, path }), Is.EqualTo(path));
            Assert.That(File.Exists(path), Is.False);
        }
        [Test]
        public void ParserRejectsMissingMalformedDuplicateAndNonHeadlessArguments()
        {
            string path = Path.Combine(Path.GetTempPath(), "poker-smoke-" + Guid.NewGuid().ToString("N") + ".json");
            string[][] cases =
            {
                null, Array.Empty<string>(), new[] { "-nographics", PracticeBuildSmoke.OutputArgument },
                new[] { PracticeBuildSmoke.OutputArgument, path },
                new[] { "-nographics", PracticeBuildSmoke.OutputArgument, "" },
                new[] { "-nographics", PracticeBuildSmoke.OutputArgument, "relative.json" },
                new[] { "-nographics", PracticeBuildSmoke.OutputArgument, path + ".txt" },
                new[] { "-nographics", PracticeBuildSmoke.OutputArgument, path, PracticeBuildSmoke.OutputArgument, path },
                new[] { "-nographics", PracticeBuildSmoke.OutputArgument, Path.Combine(path, "missing.json") }
            };
            foreach (string[] args in cases) Assert.Throws<ArgumentException>(() => PracticeBuildSmoke.ReadOutput(args));
            Assert.That(File.Exists(path), Is.False);
        }
        [Test]
        public void ExistingFileIsRejectedWithoutChangingContents()
        {
            string path = Path.Combine(Path.GetTempPath(), "poker-smoke-" + Guid.NewGuid().ToString("N") + ".json");
            bool created = false;
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { created = true; stream.WriteByte(71); }
                Assert.Throws<ArgumentException>(() => PracticeBuildSmoke.ReadOutput(new[] { "-nographics", PracticeBuildSmoke.OutputArgument, path }));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(new byte[] { 71 }));
            }
            finally { if (created) File.Delete(path); }
        }
    }
}
