#region MODULE_CONTRACT [DOMAIN(DTO): All; CONCEPT(Test): Serialization round-trip; TECH(xUnit): Fact]
/**
 * [GREP_SUMMARY]: DtoSerializationTests, xunit, test, dto, serialization, round-trip, McpJsonContext, AOT, snake_case, pascalcase
 * [STRUCTURE]: > Sample JSON -> o McpJsonContext.SearXNGResponse -> + SearXNGResponse -> = Assert(fields) |
 *               > SearchResultDto[] -> o McpJsonContext.SearchResultDtoArray -> + JSON -> = Assert(PascalCase + omit null)
 *
 * <summary>
 * [PURPOSE]: Verify AOT-safe serialization round-trips for all DTOs via McpJsonContext
 * (source-generated path, not reflection). Covers: SearXNG JSON deserialization with
 * snake_case field mapping + nullable engine, case-insensitive tolerance, SearchResultDto[]
 * PascalCase serialisation + WhenWritingNull, SearchResultDto value-equality round-trip,
 * SearchRequest PascalCase + omit null TimeRange.
 * </summary>
 * <remarks>
 * [INVARIANTS]: All tests use McpJsonContext.Default.&lt;T&gt; (source-generated serializer),
 * NOT reflection-based JsonSerializer.Deserialize&lt;T&gt;(json) without context. Sample
 * JSON is embedded as a raw-string-literal (per plan §0.6) — no file-asset dependency.
 * [RATIONALE]: Q: Why test the source-generated path instead of reflection-based serialization?
 * A: McpJsonContext.Default.&lt;T&gt; is the exact code path used in AOT-compiled production.
 * Reflection-based serialization would silently work in tests (non-AOT) but fail at AOT
 * runtime with NotSupportedException. Testing the source-generated path verifies the
 * AOT-safe pipeline (per plan §5 constraint).
 * [CHANGES]: LAST_CHANGE: M3 — initial creation (DevelopmentPlan.md §2 step 6).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Text.Json;
using McpWebSearchService.Models;
using McpWebSearchService.Serialization;

namespace McpWebSearchService.Tests.Models;

#region CLASS_DtoSerializationTests [DOMAIN(DTO): All; CONCEPT(Test): Serialization round-trip]
/// <summary>
/// [PURPOSE]: AOT-safe serialization round-trip tests for all DTOs via McpJsonContext.
/// </summary>
public class DtoSerializationTests
{
    // Sample SearXNG JSON response (embedded raw-string-literal per plan §0.6).
    // Covers: multiple results, all fields present, null engine case.
    private const string SampleSearXNGJson = /*lang=json,strict*/
        """
        {
            "results": [
                { "title": ".NET 10 Documentation", "url": "https://learn.microsoft.com/dotnet", "content": "Official .NET 10 documentation and tutorials.", "engine": "bing" },
                { "title": "SearXNG GitHub", "url": "https://github.com/searxng/searxng", "content": "SearXNG is a free internet metasearch engine.", "engine": "google" },
                { "title": "Aggregated Result", "url": "https://example.com/article", "content": "An aggregated result without explicit engine.", "engine": null }
            ]
        }
        """;

    #region METHOD_Deserialize_SearXNGResponse_MapsSnakeCaseFields [DOMAIN(DTO): SearXNG; CONCEPT(Test): Field mapping]
    /// <summary>
    /// [PURPOSE]: Verify SearXNG sample JSON (snake_case fields) deserialises to
    /// SearXNGResponse with correct PascalCase property mapping and nullable engine handling.
    /// </summary>
    [Fact]
    public void Deserialize_SearXNGResponse_MapsSnakeCaseFields()
    {
        // [IMP:7][Deserialize_SearXNGResponse_MapsSnakeCaseFields][ACT] Deserialising sample SearXNG JSON via McpJsonContext
        var response = JsonSerializer.Deserialize<SearXNGResponse>(SampleSearXNGJson, McpJsonContext.Default.SearXNGResponse);

        Assert.NotNull(response);
        Assert.Equal(3, response!.Results.Length);

        // First result: all fields present, engine = "bing"
        Assert.Equal(".NET 10 Documentation", response.Results[0].Title);
        Assert.Equal("https://learn.microsoft.com/dotnet", response.Results[0].Url);
        Assert.Equal("Official .NET 10 documentation and tutorials.", response.Results[0].Content);
        Assert.Equal("bing", response.Results[0].Engine);

        // Second result: engine = "google"
        Assert.Equal("SearXNG GitHub", response.Results[1].Title);
        Assert.Equal("https://github.com/searxng/searxng", response.Results[1].Url);
        Assert.Equal("SearXNG is a free internet metasearch engine.", response.Results[1].Content);
        Assert.Equal("google", response.Results[1].Engine);

        // Third result: engine is null (aggregated result without explicit engine)
        Assert.Equal("Aggregated Result", response.Results[2].Title);
        Assert.Equal("https://example.com/article", response.Results[2].Url);
        Assert.Equal("An aggregated result without explicit engine.", response.Results[2].Content);
        Assert.Null(response.Results[2].Engine);

        // [IMP:9][Deserialize_SearXNGResponse_MapsSnakeCaseFields][SUCCESS] Snake_case field mapping verified: 3 results, all fields mapped, null engine confirmed.
    }
    #endregion METHOD_Deserialize_SearXNGResponse_MapsSnakeCaseFields

    #region METHOD_Deserialize_SearXNGResponse_CaseInsensitive [DOMAIN(DTO): SearXNG; CONCEPT(Test): Case-insensitive deserialization]
    /// <summary>
    /// [PURPOSE]: Verify that PropertyNameCaseInsensitive = true tolerates PascalCase keys
    /// (Title/Url/Content/Engine) in SearXNG JSON.
    /// </summary>
    [Fact]
    public void Deserialize_SearXNGResponse_CaseInsensitive()
    {
        var pascalCaseJson = /*lang=json,strict*/
            """
            {
                "results": [
                    { "Title": ".NET 10 Documentation", "Url": "https://learn.microsoft.com/dotnet", "Content": "Official .NET 10 documentation and tutorials.", "Engine": "bing" }
                ]
            }
            """;

        // [IMP:7][Deserialize_SearXNGResponse_CaseInsensitive][ACT] Deserialising PascalCase-key JSON via McpJsonContext (case-insensitive)
        var response = JsonSerializer.Deserialize<SearXNGResponse>(pascalCaseJson, McpJsonContext.Default.SearXNGResponse);

        Assert.NotNull(response);
        Assert.Single(response!.Results);
        Assert.Equal(".NET 10 Documentation", response.Results[0].Title);
        Assert.Equal("https://learn.microsoft.com/dotnet", response.Results[0].Url);
        Assert.Equal("Official .NET 10 documentation and tutorials.", response.Results[0].Content);
        Assert.Equal("bing", response.Results[0].Engine);

        // [IMP:9][Deserialize_SearXNGResponse_CaseInsensitive][SUCCESS] Case-insensitive deserialization verified.
    }
    #endregion METHOD_Deserialize_SearXNGResponse_CaseInsensitive

    #region METHOD_Serialize_SearchResultDtoArray_PascalCaseAndOmitsNull [DOMAIN(DTO): MCP; CONCEPT(Test): PascalCase + WhenWritingNull]
    /// <summary>
    /// [PURPOSE]: Verify SearchResultDto[] serialises as PascalCase JSON and omits
    /// null SourceEngine via WhenWritingNull.
    /// </summary>
    [Fact]
    public void Serialize_SearchResultDtoArray_PascalCaseAndOmitsNull()
    {
        var items = new[]
        {
            new SearchResultDto
            {
                Title = ".NET 10 Documentation",
                Url = "https://learn.microsoft.com/dotnet",
                Snippet = "Official .NET 10 documentation.",
                SourceEngine = null  // should be omitted from JSON
            },
            new SearchResultDto
            {
                Title = "SearXNG GitHub",
                Url = "https://github.com/searxng/searxng",
                Snippet = "SearXNG is a free internet metasearch engine.",
                SourceEngine = "google"  // should be present in JSON
            }
        };

        // [IMP:7][Serialize_SearchResultDtoArray_PascalCaseAndOmitsNull][ACT] Serialising SearchResultDto[] via McpJsonContext
        var json = JsonSerializer.Serialize(items, McpJsonContext.Default.SearchResultDtoArray);

        // Verify PascalCase property names present
        Assert.Contains("\"Title\"", json);
        Assert.Contains("\"Url\"", json);
        Assert.Contains("\"Snippet\"", json);

        // SourceEngine = "google" for second item should be present
        Assert.Contains("\"SourceEngine\"", json);
        Assert.Contains("\"google\"", json);

        // First item has SourceEngine = null -> should be omitted via WhenWritingNull
        // The JSON should contain "Title":".NET 10 Documentation" right before or after Url/Snippet
        // but NOT "SourceEngine" in the first object. We verify by counting "SourceEngine" occurrences.
        var sourceEngineCount = CountOccurrences(json, "\"SourceEngine\"");
        Assert.Equal(1, sourceEngineCount); // only the second item (google) has it

        // [IMP:9][Serialize_SearchResultDtoArray_PascalCaseAndOmitsNull][SUCCESS] PascalCase verified, null SourceEngine omitted via WhenWritingNull.
    }
    #endregion METHOD_Serialize_SearchResultDtoArray_PascalCaseAndOmitsNull

    #region METHOD_RoundTrip_SearchResultDto_PreservesValues [DOMAIN(DTO): MCP; CONCEPT(Test): Value-equality round-trip]
    /// <summary>
    /// [PURPOSE]: Verify single SearchResultDto round-trips with value-equality via
    /// record semantics. Serialises then deserialises, asserting field-by-field equality
    /// and record-level equality.
    /// </summary>
    [Fact]
    public void RoundTrip_SearchResultDto_PreservesValues()
    {
        var original = new SearchResultDto
        {
            Title = ".NET 10 Documentation",
            Url = "https://learn.microsoft.com/dotnet",
            Snippet = "Official .NET 10 documentation and tutorials.",
            SourceEngine = "google"
        };

        // [IMP:7][RoundTrip_SearchResultDto_PreservesValues][ACT] Serialising and deserialising SearchResultDto via McpJsonContext
        var json = JsonSerializer.Serialize(original, McpJsonContext.Default.SearchResultDto);
        var deserialized = JsonSerializer.Deserialize<SearchResultDto>(json, McpJsonContext.Default.SearchResultDto);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Title, deserialized!.Title);
        Assert.Equal(original.Url, deserialized.Url);
        Assert.Equal(original.Snippet, deserialized.Snippet);
        Assert.Equal(original.SourceEngine, deserialized.SourceEngine);

        // Record value-equality
        Assert.Equal(original, deserialized);

        // [IMP:9][RoundTrip_SearchResultDto_PreservesValues][SUCCESS] Round-trip preserves all field values and record equality.
    }
    #endregion METHOD_RoundTrip_SearchResultDto_PreservesValues

    #region METHOD_Serialize_SearchRequest_PascalCase [DOMAIN(DTO): MCP; CONCEPT(Test): PascalCase + omit null TimeRange]
    /// <summary>
    /// [PURPOSE]: Verify SearchRequest serialises as PascalCase JSON with all non-null
    /// properties (Query, Categories, Language) and omits null TimeRange via WhenWritingNull.
    /// </summary>
    [Fact]
    public void Serialize_SearchRequest_PascalCase()
    {
        var request = new SearchRequest
        {
            Query = "test query",
            Categories = "general",
            Language = "ru",
            TimeRange = null  // should be omitted from JSON
        };

        // [IMP:7][Serialize_SearchRequest_PascalCase][ACT] Serialising SearchRequest via McpJsonContext
        var json = JsonSerializer.Serialize(request, McpJsonContext.Default.SearchRequest);

        // Verify PascalCase property names
        Assert.Contains("\"Query\"", json);
        Assert.Contains("\"Categories\"", json);
        Assert.Contains("\"Language\"", json);

        // Verify null TimeRange is omitted
        Assert.DoesNotContain("\"TimeRange\"", json);

        // Verify values are present
        Assert.Contains("test query", json);
        Assert.Contains("general", json);
        Assert.Contains("ru", json);

        // [IMP:9][Serialize_SearchRequest_PascalCase][SUCCESS] SearchRequest PascalCase serialisation verified, null TimeRange omitted.
    }
    #endregion METHOD_Serialize_SearchRequest_PascalCase

    #region METHOD_CountOccurrences [DOMAIN(Utility): Test; CONCEPT(Auxiliary): String counting]
    /// <summary>
    /// [PURPOSE]: Count occurrences of a substring within a string.
    /// Used to verify that null properties are omitted exactly once vs never.
    /// </summary>
    /// <param name="text">The string to search within.</param>
    /// <param name="value">The substring to count.</param>
    /// <returns>Number of occurrences of <paramref name="value"/> in <paramref name="text"/>.</returns>
    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value))
            return 0;

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
    #endregion METHOD_CountOccurrences
}
#endregion CLASS_DtoSerializationTests
