using System;
using System.IO;
using System.Linq;
using Playreel.Producer;
using UnityEngine;

namespace Playreel.Sample
{
    // Explicitly rooted direct calls exercise the shipped IL2CPP delegate path.
    // Default sample startup remains inert. No SDK Start or region setup exists here.
    public static class NativeCreatedSelfCheck
    {
        [Serializable] private sealed class Receipt
        {
            public bool passed, released, createdOnly = true, sdkStarted = false;
            public uint state;
            public ulong clockFrequency;
            public string error;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RunIfRequested()
        {
            if (!Environment.GetCommandLineArgs().Contains("--playreel-native-self-check")) return;
            string applicationRoot = Directory.GetParent(Application.dataPath).FullName;
            string output = Path.Combine(applicationRoot, "native-self-check.json");
            var receipt = new Receipt();
            ProductionNativeRun run = null;
            try {
                if (File.Exists(output)) throw new InvalidOperationException("self_check_output_exists");
                run = new ProductionNativeRun(Path.Combine(applicationRoot, "Native", "Playreel"));
                run.Load();
                if (run.Error != null) throw new InvalidOperationException(run.Error);
                receipt.clockFrequency = run.ReadClock().Frequency;
                receipt.state = run.Poll().State;
                if (receipt.clockFrequency == 0 || receipt.state != 0)
                    throw new InvalidOperationException("created_self_check_failed");
            } catch (Exception failure) {
                // Only bounded known status/type information; no paths or native dumps.
                receipt.error = failure is InvalidOperationException ? failure.Message : failure.GetType().Name;
            } finally {
                receipt.released = run == null || run.StopAndRelease();
                receipt.passed = receipt.error == null && receipt.released;
                try {
                    using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
                    using (var text = new StreamWriter(file)) text.Write(JsonUtility.ToJson(receipt, true));
                } catch { receipt.passed = false; }
                Debug.Log("PLAYREEL_CREATED_SELF_CHECK passed=" + receipt.passed + " released=" + receipt.released);
                Application.Quit(receipt.passed ? 0 : 3);
            }
        }
    }
}
