using System.Text.Json;
namespace Playkitu.Issuer;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length is not (1 or 3 or 5) || args[0] != "--seed-control") return 2;
        var mode = "pooled"; int? selected = null; var seen = new HashSet<string>();
        for (var i = 1; i < args.Length; i += 2) {
            if (!seen.Add(args[i])) return 2;
            if (args[i] == "--stream-mode" && args[i + 1] is "pooled" or "onDemand") mode = args[i + 1];
            else if (args[i] == "--video-streams" && int.TryParse(args[i + 1], out var n)) selected = n;
            else return 2;
        }
        var streams = selected ?? (mode == "onDemand" ? 0 : Deployment.DefaultVideoStreams);
        if (mode == "onDemand" ? streams != 0 : streams is < 1 or > Deployment.MaxVideoStreams) return 2;
        // Serialize the actual state model once. This command constructs no AWS
        // client, reads no credentials and can never activate admission/upload.
        // DynamoDB type tags S/N/BOOL are case-sensitive; only inner state uses Wire.Json.
        Console.WriteLine(JsonSerializer.Serialize(new {
            pk = new { S = "CONTROL" }, sk = new { S = "META" }, v = new { N = "1" },
            data = new { S = JsonSerializer.Serialize(new GlobalState {
                SchemaVersion = mode == "onDemand" ? 2 : 1,
                Slots = Enumerable.Range(0, streams).Select(_ => new StreamSlot()).ToArray()
            }, Wire.Json) },
            admissionsEnabled = new { BOOL = false }, uploadsEnabled = new { BOOL = false }
        }));
        return 0;
    }
}
