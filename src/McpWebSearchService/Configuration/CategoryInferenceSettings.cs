#region MODULE_CONTRACT [DOMAIN(Config): CategoryInference; CONCEPT(Settings): Local LLM server; TECH(Options): IOptions<T>]
/**
 * [GREP_SUMMARY]: CategoryInferenceSettings, options, configuration, local LLM, BaseUrl, Model, TimeoutSeconds, EnableFallback, Categories, AOT-safe
 * [STRUCTURE]: > Section["CategoryInference"] -> o Bind -> + CategoryInferenceSettings { BaseUrl, Model, TimeoutSeconds, EnableFallback, Categories[] } -> = IOptions<CategoryInferenceSettings>.Value
 *
 * <summary>
 * [PURPOSE]: Strongly-typed options for the local LLM server used for semantic category selection (M9,
 * SPEC §2.1). Binds the "CategoryInference" configuration section to a sealed class with public setters,
 * enabling AOT-safe configuration binding via the EnableConfigurationBindingGenerator source generator
 * (ADR-006) — no reflection.
 * </summary>
 * <remarks>
 * [INVARIANTS]: Class is sealed (trim-analyzer friendly). All properties have public setters and safe
 * non-null defaults so validation can detect unpopulated config without NullReferenceException. Categories
 * is a CONFIGURABLE closed list from which the LLM selects a category (defaults to a practical stable
 * SearXNG subset, with "general" as fallback) — the full 32-category SearXNG set may be configured if needed.
 * [RATIONALE]: Q: Why sealed class with { get; set; } instead of a positional record? A: The
 * configuration-binding source generator emits a strongly-typed binder for public settable properties on
 * sealed classes (ADR-006); positional/init-only records complicate the binder and can provoke generator
 * warnings. Q: Why is Categories configurable rather than hardcoded? A: The milestone explicitly requires a
 * non-hardcoded, configurable category list so it adapts to a specific SearXNG instance (M9 rationale).
 * [CHANGES]: LAST_CHANGE: M9 — initial creation (M9 CategoryInference milestone, ADR-032).
 * </remarks>
 */
#endregion MODULE_CONTRACT

namespace McpWebSearchService.Configuration;

#region CLASS_CategoryInferenceSettings [DOMAIN(Config): CategoryInference; CONCEPT(Settings): Local LLM server]
/// <summary>
/// [PURPOSE]: Holds connection and behaviour options for the local LLM category-inference client.
/// Bound from the "CategoryInference" config section and validated on host startup.
/// </summary>
public sealed class CategoryInferenceSettings
{
    /// <summary>
    /// [PURPOSE]: Base URL of the local OpenAI-compatible LLM server (e.g. <c><url></c>).
    /// The client posts to <c>{BaseUrl}/v1/chat/completions</c>. Must be a non-empty absolute URI.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// [PURPOSE]: Model identifier on the LLM server (e.g. <c>Qwen3.8-27B-Q8_0.gguf</c>).
    /// Sent in the chat completion request body. Must be non-empty.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// [PURPOSE]: Per-call timeout in seconds for the LLM request (default 5s — sufficient per the M9 probe,
    /// which observed ~0.6–1.1s on a local Qwen3.8-27B). On expiry the client returns null (fallback).
    /// Must be &gt; 0.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// [PURPOSE]: When true (default), an unavailable/invalid LLM result falls back to the <c>general</c>
    /// category so search never hangs. When false, an LLM failure falls back to an empty category (SearXNG's
    /// own default). Search always remains functional.
    /// </summary>
    public bool EnableFallback { get; set; } = true;

    /// <summary>
    /// [PURPOSE]: CONFIGURABLE closed list of categories the LLM is allowed to select from (default
    /// <c>it, news, science, images, videos, music, books, weather, map, general</c>). A response not in this
    /// list is treated as invalid → fallback. The full 32-category SearXNG set may be configured if needed.
    /// </summary>
    public string[] Categories { get; set; } =
        ["it", "news", "science", "images", "videos", "music", "books", "weather", "map", "general"];
}
#endregion CLASS_CategoryInferenceSettings
