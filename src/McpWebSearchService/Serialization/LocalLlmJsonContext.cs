#region MODULE_CONTRACT [DOMAIN(Serialization): LocalLlm; CONCEPT(AOT): Source-generated context for LLM DTOs; TECH(AOT): JsonSerializerContext]
/**
 * [GREP_SUMMARY]: LocalLlmJsonContext, JsonSerializerContext, source generator, AOT, JsonSerializable, OpenAI chat completions
 * [STRUCTURE]: > LocalLlmDto types -> o [JsonSerializable] x2 -> + LocalLlmJsonContext (partial) -> = compile-time JsonTypeInfo<T> -> o LocalLlmCategoryInference -> + AOT-safe LLM serialization
 *
 * <summary>
 * [PURPOSE]: AOT-safe JsonSerializerContext source generator for the OpenAI-compatible chat completions
 * DTOs (ChatCompletionRequest / ChatCompletionResponse) that cross the JSON boundary with the local LLM
 * server. Isolated from McpJsonContext (which owns the SearXNG/MCP DTOs) so the two wire formats do not mix.
 * </summary>
 * <remarks>
 * [INVARIANTS]: All boundary-crossing types (ChatCompletionRequest, ChatCompletionResponse) are registered
 * with [JsonSerializable]; their reachable nested types (ChatMessage, Choice, ChatTemplateKwargs) are
 * discovered transitively by the source generator. No reflection-based serialization — AOT-safe.
 * [RATIONALE]: Q: Why a dedicated context? A: The local-LLM payload is a separate concern with a snake_case
 * wire format distinct from McpJsonContext's PascalCase MCP DTOs. A dedicated context keeps the AOT
 * registration explicit and the two boundaries decoupled (mirrors ADR-014's single-context-per-concern).
 * [CHANGES]: LAST_CHANGE: M9 — initial creation (M9 CategoryInference milestone, ADR-032).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Text.Json.Serialization;
using McpWebSearchService.Models;

namespace McpWebSearchService.Serialization;

#region CLASS_LocalLlmJsonContext [DOMAIN(Serialization): LocalLlm; CONCEPT(AOT): Source generator]
/// <summary>
/// [PURPOSE]: AOT-safe JsonSerializerContext source generator — registers the local-LLM chat DTOs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,  // [JsonPropertyName] per property defines the wire format
    PropertyNameCaseInsensitive = true,                        // tolerant deserialization of LLM responses
    WriteIndented = false,                                     // wire-compact request body
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(ChatCompletionRequest))]
[JsonSerializable(typeof(ChatCompletionResponse))]
internal partial class LocalLlmJsonContext : JsonSerializerContext
{
    // Body intentionally empty — source generator emits JsonTypeInfo<T> at compile time.
}
#endregion CLASS_LocalLlmJsonContext
