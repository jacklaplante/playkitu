using System.Text.Json;
using NUnit.Framework;
namespace Issuer.Tests;
[TestFixture]
public sealed class SeedControlTests
{
    [Test] public void OfflineSeedRetainsDynamoTypeCasingAndCanonicalOffState()
    {
        using var output = new StringWriter(); var before = Console.Out;
        try { Console.SetOut(output); Assert.That(Playkitu.Issuer.Program.Main(["--seed-control"]), Is.Zero); }
        finally { Console.SetOut(before); }
        using var doc = JsonDocument.Parse(output.ToString()); var root = doc.RootElement;
        Assert.That(root.GetProperty("pk").GetProperty("S").GetString(), Is.EqualTo("CONTROL"));
        Assert.That(root.GetProperty("v").GetProperty("N").GetString(), Is.EqualTo("1"));
        Assert.That(root.GetProperty("admissionsEnabled").GetProperty("BOOL").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("uploadsEnabled").GetProperty("BOOL").GetBoolean(), Is.False);
        using var state = JsonDocument.Parse(root.GetProperty("data").GetProperty("S").GetString()!);
        Assert.That(state.RootElement.GetProperty("slots").GetArrayLength(), Is.EqualTo(4));
        Assert.That(state.RootElement.GetProperty("active").EnumerateObject().Count(), Is.Zero);
    }
}
