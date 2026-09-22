using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bond.Parser.Json;
using Bond.Parser.Parser;
using FluentAssertions;

namespace Bond.Parser.Tests;

public class JsonSnapshotTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        // These JSON outputs from upstream drop the Unary streaming marker
        "example.bond",
        "generic_service.bond",
        "service_attributes.bond",
        "service_inheritance.bond",
    };

    public static IEnumerable<object[]> FixturePairs()
    {
        foreach (var bondPath in Directory.EnumerateFiles(FixturesDir, "*.bond"))
        {
            if (Skipped.Contains(Path.GetFileName(bondPath)))
            {
                continue;
            }

            var jsonPath = Path.ChangeExtension(bondPath, ".json");
            if (File.Exists(jsonPath))
            {
                yield return [bondPath, jsonPath];
            }
        }
    }

    [Theory]
    [MemberData(nameof(FixturePairs))]
    public async Task BondAst_Json_MatchesFixture(string bondPath, string jsonPath)
    {
        var result = await ParserFacade.ParseFileAsync(bondPath, cancellationToken: TestContext.Current.CancellationToken);
        result.Success.Should().BeTrue($"fixture {bondPath} should parse");

        var options = BondJsonSerializerOptions.GetOptions();
        var actual = JsonSerializer.Serialize(result.Ast!, options);

        // Normalize both sides for stable comparison (order-insensitive for objects and arrays)
        using var actualDoc = JsonDocument.Parse(actual);
        using var expectedDoc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, TestContext.Current.CancellationToken));

        Canonicalize(actualDoc.RootElement)
            .Should().Be(Canonicalize(expectedDoc.RootElement));
    }

    private static string Canonicalize(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",",
                element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => $"\"{p.Name}\":{Canonicalize(p.Value)}")) + "}",

            JsonValueKind.Array => "[" + string.Join(",",
                element.EnumerateArray()
                    .Select(Canonicalize)
                    .OrderBy(s => s, StringComparer.Ordinal)) + "]",

            _ => element.GetRawText()
        };
}
