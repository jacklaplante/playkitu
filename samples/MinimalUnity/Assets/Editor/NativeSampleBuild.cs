using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Playreel.Sample.Editor
{
    public static class NativeSampleBuild
    {
        [Serializable] private sealed class Receipt
        {
            public string result, error, executable;
            public bool backendRestored, runtimeIdentical;
            public int errors;
        }

        // Batchmode: -executeMethod Playreel.Sample.Editor.NativeSampleBuild.Build
        // -playreelOutput <fresh absolute path> -playreelRuntime <absolute runtime>.
        public static void Build()
        {
            string[] args = Environment.GetCommandLineArgs();
            int outputIndex = Array.IndexOf(args, "-playreelOutput"), runtimeIndex = Array.IndexOf(args, "-playreelRuntime");
            if (outputIndex < 0 || runtimeIndex < 0 || outputIndex + 1 >= args.Length || runtimeIndex + 1 >= args.Length)
                throw new InvalidOperationException("Explicit sample output and native runtime are required.");
            string output = args[outputIndex + 1], runtime = args[runtimeIndex + 1];
            if (!Path.IsPathRooted(output) || !Path.IsPathRooted(runtime) || Directory.Exists(output) || !Directory.Exists(runtime))
                throw new InvalidOperationException("Fresh absolute output and existing absolute runtime required.");
            var previous = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var receipt = new Receipt { executable = Path.Combine(output, "player", "PlayreelSample.exe") };
            Directory.CreateDirectory(output);
            try {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
                    scenes = new[] { "Assets/OfflineConsent.unity" }, locationPathName = receipt.executable,
                    target = BuildTarget.StandaloneWindows64, options = BuildOptions.None
                });
                receipt.result = report.summary.result.ToString(); receipt.errors = report.summary.totalErrors;
                if (report.summary.result != BuildResult.Succeeded || report.summary.totalErrors != 0)
                    throw new InvalidOperationException("Minimal IL2CPP sample build failed.");
                string destination = Path.Combine(output, "player", "Native", "Playreel");
                foreach (string file in Directory.GetFiles(runtime, "*", SearchOption.AllDirectories)) {
                    string copy = Path.Combine(destination, file.Substring(runtime.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)); File.Copy(file, copy);
                }
                var sourceFiles = Directory.GetFiles(runtime, "*", SearchOption.AllDirectories);
                receipt.runtimeIdentical = sourceFiles.Length == Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length
                    && sourceFiles.All(file => Hash(file) == Hash(Path.Combine(destination, file.Substring(runtime.Length + 1))));
                if (!receipt.runtimeIdentical) throw new InvalidOperationException("Sample runtime copy mismatch.");
            } catch (Exception error) { receipt.error = error.GetType().Name + ": " + error.Message; }
            finally {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, previous);
                receipt.backendRestored = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone) == previous;
                File.WriteAllText(Path.Combine(output, "build-result.json"), JsonUtility.ToJson(receipt, true));
            }
            if (receipt.error != null || !receipt.backendRestored) throw new InvalidOperationException("Sample build failed; inspect build-result.json.");
        }

        private static string Hash(string path)
        {
            using (var algorithm = SHA256.Create()) using (var file = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(file));
        }
    }
}
