using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;
using Playkitu.Issuer;

namespace Playkitu.Tests;

// Optional private export from the actual accelerated Unity uploader. No fixture
// regeneration or raw timing edits: validate/use the exact exported gzip bytes.
public sealed record GeneratedDiagnosticObject(string Key, string Kind, int Sequence, long CompressedBytes, string Sha256Base64)
{
    public ObjectDescriptor Descriptor => new(Kind, Sequence, CompressedBytes, Sha256Base64);
}
public sealed record GeneratedDiagnosticsFixture(Guid SessionId, GeneratedDiagnosticObject[] Objects)
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("SBREC_FOUR_HOUR_FIXTURE")
        ?? throw new InvalidOperationException("Explicit private four-hour fixture required.");
    public static GeneratedDiagnosticsFixture Load()
    {
        if (Environment.GetEnvironmentVariable("SBREC_FOUR_HOUR_FIXTURE") is null)
            Assert.Ignore("Set SBREC_FOUR_HOUR_FIXTURE to the frozen production client export.");
        return JsonSerializer.Deserialize<GeneratedDiagnosticsFixture>(File.ReadAllBytes(Path.Combine(DirectoryPath, "objects.json")), Wire.Json)!;
    }
    public static string FilePath(GeneratedDiagnosticObject item)
    {
        var root = Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, item.Key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid fixture path.");
        return path;
    }
    public async Task VerifyAsync()
    {
        foreach (var item in Objects) {
            item.Descriptor.Validate(); var path = FilePath(item);
            Assert.That(new FileInfo(path).Length, Is.EqualTo(item.CompressedBytes));
            await using var file = File.OpenRead(path);
            Assert.That(Convert.ToBase64String(await SHA256.HashDataAsync(file)), Is.EqualTo(item.Sha256Base64));
        }
    }
}
