#region MODULE_CONTRACT [DOMAIN(Serialization): System.Text.Json; CONCEPT(AOT): Source generator; TECH(AOT): JsonSerializerContext]
/**
 * [GREP_SUMMARY]: McpJsonContext, JsonSerializerContext, source generator, AOT, JsonSerializable, JsonSourceGenerationOptions
 * [STRUCTURE]: > DTO types -> o [JsonSerializable] x6 -> + McpJsonContext (partial) -> = compile-time JsonTypeInfo<T> -> o M4/M5/M6 -> + AOT-safe serialization
 *
 * <summary>
 * [PURPOSE]: Single AOT-safe JsonSerializerContext source generator for ALL DTOs crossing
 * the JSON boundary (SearXNG HTTP response, MCP tool response). Eliminates reflection-based
 * serialisation, enabling Native AOT without IL trim warnings. Six [JsonSerializable]
 * registrations cover the 4 DTO types plus 2 array variants (required — source generator
 * does not auto-derive arrays from element types).
 * </summary>
 * <remarks>
 * [INVARIANTS]: All DTOs that cross a JSON boundary are registered here (SearchResultDto,
 * SearchRequest, SearXNGResponse, SearXNGResult) plus SearchResultDto[] and SearXNGResult[].
 * Array variants are explicitly registered — the source generator does not automatically
 * derive them. PropertyNamingPolicy=Unspecified keeps PascalCase for MCP DTOs; SearXNG
 * DTOs override via [JsonPropertyName] per property.
 * [RATIONALE]: Q: Why a single context instead of separate MCP/SearXNG contexts?
 * A: Both sets of DTOs share the same serialization options (PascalCase default, SearXNG
 * fields overridden by [JsonPropertyName]). Splitting would duplicate options config
 * and complicate DI wiring in M6. Single context is simpler (per plan §0.4).
 * [CHANGES]: LAST_CHANGE: M3 — initial creation (DevelopmentPlan.md §2 step 5).
 * [IMP:10][McpJsonContext][CREATE] AI Belief State: Context created with 6 registrations.
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Text.Json;
using System.Text.Json.Serialization;
using McpWebSearchService.Models;

namespace McpWebSearchService.Serialization;

#region CLASS_McpJsonContext [DOMAIN(Serialization): System.Text.Json; CONCEPT(AOT): Source generator]
/// <summary>
/// [PURPOSE]: AOT-safe JsonSerializerContext source generator — registers all DTOs
/// crossing the JSON boundary (4 DTO types + 2 array variants = 6 [JsonSerializable]).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,  // PascalCase default; SearXNG DTOs override via [JsonPropertyName]
    PropertyNameCaseInsensitive = true,                         // tolerant deserialization of SearXNG JSON (Title/title/TITLE)
    WriteIndented = false,                                      // wire-compact (MCP stdio, HTTP)
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // omit null SourceEngine/TimeRange in MCP response
    GenerationMode = JsonSourceGenerationMode.Default)]         // both serialize + deserialize
[JsonSerializable(typeof(SearchResultDto))]
[JsonSerializable(typeof(SearchResultDto[]))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearXNGResponse))]
[JsonSerializable(typeof(SearXNGResult))]
[JsonSerializable(typeof(SearXNGResult[]))]
public partial class McpJsonContext : JsonSerializerContext
{
    // Body intentionally empty — source generator emits JsonTypeInfo<T> properties
    // for each [JsonSerializable] type at compile time.
}
#endregion CLASS_McpJsonContext
