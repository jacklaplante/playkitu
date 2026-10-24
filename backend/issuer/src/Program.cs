using System.Text.Json;
namespace Playkitu.Issuer;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--seed-control") return 2;
        // Serialize the actual state model once. This command constructs no AWS
        // client, reads no credentials and can never activate admission/upload.
        // DynamoDB type tags S/N/BOOL are case-sensitive; only inner state uses Wire.Json.
        Console.WriteLine(JsonSerializer.Serialize(new {
            pk = new { S = "CONTROL" }, sk = new { S = "META" }, v = new { N = "1" },
            data = new { S = JsonSerializer.Serialize(new GlobalState(), Wire.Json) },
            admissionsEnabled = new { BOOL = false }, uploadsEnabled = new { BOOL = false }
        }));
        return 0;
    }
}
