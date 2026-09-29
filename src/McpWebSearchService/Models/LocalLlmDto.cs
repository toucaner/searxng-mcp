#region MODULE_CONTRACT [DOMAIN(DTO): LocalLlm; CONCEPT(DTO): OpenAI-compatible chat completions request/response; TECH(AOT): JsonSerializerContext]
/**
 * [GREP_SUMMARY]: LocalLlmDto, ChatCompletionRequest, ChatMessage, ChatCompletionResponse, Choice, ChatTemplateKwargs, OpenAI-compatible, AOT-safe
 * [STRUCTURE]: > CategoryInferenceSettings -> o LocalLlmCategoryInference -> + ChatCompletionRequest -> o POST /v1/chat/completions -> = ChatCompletionResponse -> o ValidateCategory -> = string? category
 *
 * <summary>
 * [PURPOSE]: Internal DTOs for the OpenAI-compatible <c>/v1/chat/completions</c> API consumed by
 * <c>LocalLlmCategoryInference</c> (M9 semantic category selection). These DTOs cross the JSON boundary
 * only with the local LLM server, so they are registered in a dedicated source-generated
 * <c>LocalLlmJsonContext</c> (never reflection). Wire format uses snake_case <c>[JsonPropertyName]</c>
 * mappings to match the OpenAI-compatible request/response schema exactly.
 * </summary>
 * <remarks>
 * [INVARIANTS]: All DTOs are internal records/classes with [JsonPropertyName] set per property to match
 * the OpenAI wire format. Date-only/number fields are non-nullable with safe defaults. Response DTOs are
 * tolerant (nullable Choices/Message) so a malformed server response yields null rather than a throw.
 * [RATIONALE]: Q: Why a dedicated context and DTOs rather than reusing McpJsonContext? A: The OpenAI chat
 * completions payload is a distinct serialization concern from the SearXNG/MCP DTOs (different wire format,
 * snake_case). A separate <c>LocalLlmJsonContext</c> keeps the two boundaries isolated (mirrors the
 * single-context-per-concern discipline in ADR-014). Q: Why records? A: Consistent with the existing
 * Models/ convention — immutable data carriers crossed by the source generator.
 * [CHANGES]: LAST_CHANGE: M9 — initial creation (M9 CategoryInference milestone).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Text.Json.Serialization;

namespace McpWebSearchService.Models;

#region RECORD_ChatTemplateKwargs [DOMAIN(DTO): LocalLlm; CONCEPT(DTO): Qwen3 thinking disable flag]
/// <summary>
/// [PURPOSE]: Extra per-request kwargs forwarded verbatim to the LLM. <c>enable_thinking: false</c>
/// is critical for reasoning models (Qwen3) — without it they consume tokens on reasoning and never
/// return a single-word classification within the timeout (verified in the M9 practical probe).
/// </summary>
internal sealed class ChatTemplateKwargs
{
    /// <summary>When false, disables the model's reasoning/thinking phase (Qwen3).</summary>
    [JsonPropertyName("enable_thinking")]
    public bool EnableThinking { get; set; }
}
#endregion RECORD_ChatTemplateKwargs

#region RECORD_ChatMessage [DOMAIN(DTO): LocalLlm; CONCEPT(DTO): One chat message]
/// <summary>
/// [PURPOSE]: A single chat message in the conversation (<c>role</c> + <c>content</c>).
/// </summary>
internal sealed class ChatMessage
{
    /// <summary>Message role: "system" or "user".</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>Message text content.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
#endregion RECORD_ChatMessage

#region RECORD_ChatCompletionRequest [DOMAIN(DTO): LocalLlm; CONCEPT(DTO): Outbound chat request]
/// <summary>
/// [PURPOSE]: Outbound OpenAI-compatible chat completion request. Uses <c>temperature: 0</c> for
/// determinism, a small <c>max_tokens</c> to force a single-word answer, and
/// <c>chat_template_kwargs.enable_thinking: false</c> for reasoning-model compatibility.
/// </summary>
internal sealed class ChatCompletionRequest
{
    /// <summary>The model identifier on the LLM server (e.g. "Qwen3.8-27B-Q8_0.gguf").</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Conversation messages — typically one system + one user message.</summary>
    [JsonPropertyName("messages")]
    public ChatMessage[] Messages { get; set; } = [];

    /// <summary>Sampling temperature — 0 for deterministic single-word classification.</summary>
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0;

    /// <summary>Maximum completion tokens — small to force a single category word.</summary>
    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 10;

    /// <summary>Extra per-model kwargs, notably enable_thinking:false for Qwen3 (M9 probe).</summary>
    [JsonPropertyName("chat_template_kwargs")]
    public ChatTemplateKwargs ChatTemplateKwargs { get; set; } = new();
}
#endregion RECORD_ChatCompletionRequest

#region RECORD_Choice [DOMAIN(DTO): LocalLlm; CONCEPT(DTO): One response choice]
/// <summary>
/// [PURPOSE]: One completion choice in the server response — contains the assistant message and finish reason.
/// </summary>
internal sealed class Choice
{
    /// <summary>The assistant message (holds the single-word category in Content).</summary>
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }

    /// <summary>Finish reason: "stop" on a complete single-token answer, "length" on truncation.</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}
#endregion RECORD_Choice

#region RECORD_ChatCompletionResponse [DOMAIN(DTO): LocalLlm; CONCEPT(DTO): Inbound chat response]
/// <summary>
/// [PURPOSE]: Inbound OpenAI-compatible chat completion response. <c>Choices</c> is nullable-tolerant so a
/// malformed/empty response yields null category (caller falls back to general).
/// </summary>
internal sealed class ChatCompletionResponse
{
    /// <summary>Completion choices — we read <c>choices[0].message.content</c>.</summary>
    [JsonPropertyName("choices")]
    public Choice[] Choices { get; set; } = [];
}
#endregion RECORD_ChatCompletionResponse
