using System;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Poker.Editor
{
    /// <summary>Checks the font notice shipped as a readable file inside every desktop player.</summary>
    public static class HoldemBuildNotices
    {
        public const string RelativeNoticePath = "ThirdPartyLicenses/NanumGothic-OFL.txt";
        private const string SourcePath = "Poker/Resources/Fonts/OFL.txt";

        public static void ValidateSource(string assetsRoot)
            => ValidateCopy(Path.Combine(assetsRoot, SourcePath),
                Path.Combine(assetsRoot, "StreamingAssets", RelativeNoticePath));

        public static void ValidatePlayer(string assetsRoot, BuildTarget target, string outputPath)
        {
            string data;
            if (target == BuildTarget.StandaloneOSX)
                data = Path.Combine(outputPath, "Contents", "Resources", "Data");
            else if (target == BuildTarget.StandaloneWindows64)
                data = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)),
                    Path.GetFileNameWithoutExtension(outputPath) + "_Data");
            else throw new ArgumentOutOfRangeException(nameof(target));
            ValidateCopy(Path.Combine(assetsRoot, SourcePath), Path.Combine(data, "StreamingAssets", RelativeNoticePath));
        }

        private static void ValidateCopy(string source, string copy)
        {
            if (!File.Exists(source) || !File.Exists(copy))
                throw new InvalidOperationException("The original or bundled NanumGothic font license is missing.");
            byte[] original = File.ReadAllBytes(source);
            if (original.Length == 0 || !original.SequenceEqual(File.ReadAllBytes(copy)))
                throw new InvalidOperationException("The bundled NanumGothic font license must match the original file exactly.");
        }
    }
}
