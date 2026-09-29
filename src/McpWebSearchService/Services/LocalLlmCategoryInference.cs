#region MODULE_CONTRACT [DOMAIN(HTTP): LocalLlmCategoryInference; CONCEPT(Typed Client): OpenAI-compatible chat client + strict validation; TECH(M9): source-gen JsonSerializerContext]
/**
 * [GREP_SUMMARY]: LocalLlmCategoryInference, ICategoryInferenceClient, OpenAI-compatible, /v1/chat/completions, chat_template_kwargs, enable_thinking, temperature 0, max_tokens, never-throw, null on failure
 * [STRUCTURE]: > query + settings -> o BuildRequest() = ChatCompletionRequest -> o POST {BaseUrl}/v1/chat/completions = ChatCompletionResponse -> o ValidateCategory() -> = string? category | > any failure -> null
 *
 * <summary>
 * [PURPOSE]: Typed HTTP client (M9) that posts an OpenAI-compatible <c>/v1/chat/completions</c> request to a
 * local LLM server to classify a search query into one SearXNG category. AOT-safe: request/response use the
 * source-generated <c>LocalLlmJsonContext</c>, no reflection/SDK libs. STRICT barrier: the parsed
 * <c>choices[0].message.content</c> is validated to be a single token from the CONFIGURED closed
 * CategoryInferenceSettings.Categories list; anything else is rejected. NEVER throws — every failure mode
 * (timeout, HTTP, bad JSON, invalid content) returns null so the caller falls back to general.
 * </summary>
 * <remarks>
 * [INVARIANTS]: InferCategoryAsync NEVER throws (returns null on every failure path). Response is strictly
 * validated against the configured closed category set — a raw LLM word outside the set is treated as invalid
 * (null). Per-call timeout = CategoryInferenceSettings.TimeoutSeconds (linked CTS); on expiry returns null.
 * LDD logging covers every path with [IMP:5-9] markers.
 * [RATIONALE]: Q: Why OpenAI-compatible /v1/chat/completions rather than a raw /api/generate? A: Milestone
 * requires OpenAI-compatible format (the verified probe server exposed /v1/chat/completions); the generic
 * format is fully covered by the request DTO (model, messages, temperature, max_tokens). Q: Why must the
 * client send <c>chat_template_kwargs: {"enable_thinking": false}</c>? A: M9 practical probe — a Qwen3
 * reasoning model without it consumes tokens on a long reasoning phase (~9.5-10s, finish_reason=length) and
 * never returns a single-word content, exceeding both the 5s timeout and SearXNG latency budgets (ADR-032).
 * Q: Why never throw? A: Category inference is best-effort decoration; a failure must not break web_search.
 * [CHANGES]: LAST_CHANGE: M9-fix — SystemPrompt improved (IT-priority rule + few-shot examples) per A/B
 * probe against the local LLM (10/10 correct vs 9/10 for the old prompt); old prompt defaulted IT queries
 * to "general", causing 0-result SearXNG searches (M8 instability).
 * [IMP:10][LocalLlmCategoryInference][CREATE] AI Belief State: AOT-safe OpenAI-compatible client, strict
 * closed-set validation, null-on-every-failure contract.
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using McpWebSearchService.Configuration;
using McpWebSearchService.Models;
using McpWebSearchService.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpWebSearchService.Services;

#region CLASS_LocalLlmCategoryInference [DOMAIN(HTTP): LocalLlmCategoryInference; CONCEPT(Typed Client): OpenAI-compatible chat client]
/// <summary>
/// [PURPOSE]: Typed client that classifies a search query into one SearXNG category via a local
/// OpenAI-compatible LLM server (M9). AOT-safe, strict closed-set validation, null-on-failure.
/// </summary>
public sealed class LocalLlmCategoryInference : ICategoryInferenceClient
{
    #region CONSTS [DOMAIN(HTTP): Prompt + request constants]
    /// <summary>System prompt — instructs the model to answer with exactly one category word, with an
    /// IT-priority rule + few-shot examples (M9 A/B probe: without it the model defaults to "general"
    /// even for clearly-IT queries like ".NET AOT").</summary>
    private const string SystemPrompt =
        "You are a web-search category classifier for a search engine. Given a search query, reply with " +
        "EXACTLY ONE WORD: the single most appropriate category from the allowed set. Reply only with the " +
        "category word and nothing else - no punctuation, no explanation.\n" +
        "\n" +
        "Priority: if the query is about programming, software, development, technology, hardware, or IT -> " +
        "reply 'it'. Prefer a specific category when clearly applicable (news, science, images, videos, music, " +
        "books, weather, map). Use 'general' ONLY when no specific category fits.\n" +
        "\n" +
        "Examples:\n" +
        "dotnet 10 native aot best practices -> it\n" +
        "python async tutorial -> it\n" +
        "what is the capital of France -> general\n" +
        "breaking news today -> news\n" +
        "cute cat pictures -> images";

    /// <summary>Maximum completion tokens — forced small so the model emits a single word.</summary>
    private const int MaxTokens = 10;

    /// <summary>Deterministic sampling temperature.</summary>
    private const double Temperature = 0.0;
    #endregion CONSTS

    #region FIELDS [DOMAIN(HTTP): Injected dependencies]
    private readonly HttpClient _httpClient;
    private readonly CategoryInferenceSettings _settings;
    private readonly ILogger<LocalLlmCategoryInference> _logger;
    private readonly Uri _completionsUri;
    #endregion FIELDS

    #region CTOR_LocalLlmCategoryInference [DOMAIN(HTTP): Constructor]
    /// <summary>
    /// [PURPOSE]: Constructs the typed LLM client with injected dependencies and precomputes the
    /// completions endpoint URI from the configured BaseUrl.
    /// </summary>
    /// <param name="httpClient">HttpClient from IHttpClientFactory — the per-call timeout is applied via a
    /// linked CancellationTokenSource (settings.TimeoutSeconds), not HttpClient.Timeout.</param>
    /// <param name="options">CategoryInferenceSettings bound from the "CategoryInference" config section.</param>
    /// <param name="logger">Structured logger for LDD trace logging [IMP:5-9].</param>
    public LocalLlmCategoryInference(
        HttpClient httpClient,
        IOptions<CategoryInferenceSettings> options,
        ILogger<LocalLlmCategoryInference> logger)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _logger = logger;

        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        _completionsUri = new Uri($"{baseUrl}/v1/chat/completions", UriKind.Absolute);
    }
    #endregion CTOR_LocalLlmCategoryInference

    #region METHOD_InferCategoryAsync [DOMAIN(HTTP): Main entry point]
    /// <summary>
    /// [PURPOSE]: Classifies a query into one allowed category via the local LLM. Builds the request,
    /// POSTs to /v1/chat/completions, parses choices[0].message.content, and STRICTLY validates it against the
    /// configured closed set. Every failure path returns null (never throws).
    /// </summary>
    /// <param name="query">The search query whose category is inferred.</param>
    /// <param name="ct">Cancellation token — linked with a per-call timeout derived from settings.TimeoutSeconds.</param>
    /// <returns>The inferred category (member of the closed set), or null on any failure/invalid response.</returns>
    public async Task<string?> InferCategoryAsync(string query, CancellationToken ct = default)
    {
        _logger.LogInformation("[IMP:5][InferCategory][START] query=\"{Query}\", timeout={TimeoutSeconds}s", query, _settings.TimeoutSeconds);

        try
        {
            #region STEP_TIMEOUT [DOMAIN(HTTP): Per-call timeout via linked CTS]
            // Apply the configured per-call timeout independently of any caller token.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));
            #endregion STEP_TIMEOUT

            #region STEP_BUILD_REQUEST [DOMAIN(HTTP): Construct OpenAI-compatible request]
            var request = BuildRequest(query);
            var json = JsonSerializer.Serialize(request, LocalLlmJsonContext.Default.ChatCompletionRequest);
            _logger.LogInformation("[IMP:6][InferCategory][REQUEST] POST {Uri} model=\"{Model}\"", _completionsUri, request.Model);
            #endregion STEP_BUILD_REQUEST

            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            #region STEP_POST [DOMAIN(HTTP): Dispatch request]
            var response = await _httpClient.PostAsync(_completionsUri, content, cts.Token).ConfigureAwait(false);
            #endregion STEP_POST

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[IMP:8][InferCategory][HTTP] status={StatusCode} — treating as unavailable", response.StatusCode);
                return null;
            }

            #region STEP_PARSE [DOMAIN(HTTP): Deserialize + validate]
            var completion = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
                LocalLlmJsonContext.Default.ChatCompletionResponse, cts.Token).ConfigureAwait(false);

            var category = ValidateCategory(completion);

            if (category != null)
            {
                _logger.LogInformation("[IMP:9][InferCategory][DONE] category=\"{Category}\"", category);
            }
            else
            {
                _logger.LogWarning("[IMP:8][InferCategory][INVALID] LLM response not in closed category set — falling back");
            }
            return category;
            #endregion STEP_PARSE
        }
        catch (OperationCanceledException)
        {
            // Timeout (linked CTS) or caller cancellation — category inference is best-effort, return null.
            _logger.LogWarning("[IMP:8][InferCategory][TIMEOUT/CANCEL] exceeded {TimeoutSeconds}s or cancelled", _settings.TimeoutSeconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[IMP:8][InferCategory][HTTPERR] {Message}", ex.Message);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[IMP:8][InferCategory][JSONERR] invalid LLM response JSON");
            return null;
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, "[IMP:8][InferCategory][URI] invalid completions URI {Uri}", _completionsUri);
            return null;
        }
    }
    #endregion METHOD_InferCategoryAsync

    #region PRIVATE_METHOD_BuildRequest [DOMAIN(HTTP): Request factory]
    /// <summary>
    /// [PURPOSE]: Builds an OpenAI-compatible ChatCompletionRequest for single-word category classification:
    /// system prompt (single-word instruction) + user message (the query), temperature 0, small max_tokens,
    /// and chat_template_kwargs { enable_thinking: false } for reasoning-model compatibility (M9 probe).
    /// </summary>
    private ChatCompletionRequest BuildRequest(string query)
    {
        var allowed = _settings.Categories.Length == 0
            ? "general"
            : string.Join(", ", _settings.Categories);

        return new ChatCompletionRequest
        {
            Model = _settings.Model,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            ChatTemplateKwargs = new ChatTemplateKwargs { EnableThinking = false },
            Messages =
            [
                new ChatMessage { Role = "system", Content = $"{SystemPrompt} Allowed categories: {allowed}." },
                new ChatMessage { Role = "user", Content = query }
            ]
        };
    }
    #endregion PRIVATE_METHOD_BuildRequest

    #region PRIVATE_METHOD_ValidateCategory [DOMAIN(HTTP): Strict closed-set validation]
    /// <summary>
    /// [PURPOSE]: Extracts choices[0].message.content and validates it is a single token from the CONFIGURED
    /// closed category set (case-insensitive, trimmed). Returns the canonical (lowercase) category, or null if
    /// the response is malformed, empty, multi-word, or out-of-set.
    /// </summary>
    private string? ValidateCategory(ChatCompletionResponse? completion)
    {
        if (completion?.Choices is null || completion.Choices.Length == 0)
            return null;

        var content = completion.Choices[0]?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            return null;

        // Trim, lowercase, and require a single clean token (no spaces/punctuation).
        var candidate = content.Trim().ToLowerInvariant();
        if (candidate.Length == 0 || candidate.IndexOf(' ') >= 0)
            return null;

        foreach (var allowed in _settings.Categories)
        {
            if (string.Equals(allowed, candidate, StringComparison.OrdinalIgnoreCase))
                return allowed.ToLowerInvariant();
        }

        return null;
    }
    #endregion PRIVATE_METHOD_ValidateCategory
}
#endregion CLASS_LocalLlmCategoryInference
