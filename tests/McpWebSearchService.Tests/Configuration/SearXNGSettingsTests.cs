#region MODULE_CONTRACT [DOMAIN(Config): SearXNG; CONCEPT(Test): Settings binding + validation; TECH(xUnit): Fact/Theory]
/**
 * [GREP_SUMMARY]: SearXNGSettingsTests, xunit, test, searxng, settings, binding, validation, defaults, OptionsValidationException
 * [STRUCTURE]: > Config(dict) -> o Configure<SearXNGSettings> -> + IOptions.Value -> = Assert(fields) | > Invalid -> o ValidateOnStart -> + OptionsValidationException
 *
 * <summary>
 * [PURPOSE]: Verify SearXNGSettings configuration binding, defaults, and validation rules
 * (DevelopmentPlan.md §3 AC1/AC6/AC8). Covers: value binding (all 4 fields), defaults under empty
 * config, positive validation, and negative validation (empty BaseUrl, MaxResults&lt;=0, empty
 * DefaultLanguage, invalid URI).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Tests use ConfigurationBuilder with in-memory dictionary (no real appsettings.json).
 * Validation is exercised via OptionsBuilder.Validate + ValidateOnStart through a ServiceProvider,
 * which throws OptionsValidationException when invalid config is accessed.
 * [RATIONALE]: Q: Why not just assert on the Validate lambda result? A: ValidateOnStart wires the
 * validators into the Options pipeline; resolving IOptions&lt;T&gt;.Value from a built provider
 * triggers the same validation path the host uses at startup — this is the closest unit-level
 * equivalent to the runtime behaviour verified in AC5.
 * [CHANGES]: LAST_CHANGE: M2 — initial creation (DevelopmentPlan.md §2 step 7).
 * </remarks>
 */
#endregion MODULE_CONTRACT

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using McpWebSearchService.Configuration;

namespace McpWebSearchService.Tests.Configuration;

#region CLASS_SearXNGSettingsTests [DOMAIN(Config): SearXNG; CONCEPT(Test): Binding + validation]
/// <summary>
/// [PURPOSE]: Unit tests for <see cref="SearXNGSettings"/> binding, defaults, and validation.
/// </summary>
public class SearXNGSettingsTests
{
    #region METHOD_Build_ReturnsBoundValues [DOMAIN(Config): SearXNG; TECH(Options): Bind]
    /// <summary>
    /// [PURPOSE]: Verify all four SearXNG fields bind correctly from an in-memory configuration.
    /// </summary>
    [Fact]
    public void Build_ReturnsBoundValues_ForAllFourFields()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SearXNG:BaseUrl"] = "http://searxng-instance:8080",
                ["SearXNG:MaxResults"] = "5",
                ["SearXNG:BlockedDomains:0"] = "pinterest.com",
                ["SearXNG:BlockedDomains:1"] = "facebook.com",
                ["SearXNG:DefaultLanguage"] = "ru-RU",
            })
            .Build();

        var settings = config.GetSection("SearXNG").Get<SearXNGSettings>();

        Assert.NotNull(settings);
        Assert.Equal("http://searxng-instance:8080", settings!.BaseUrl);
        Assert.Equal(5, settings.MaxResults);
        Assert.Equal(new[] { "pinterest.com", "facebook.com" }, settings.BlockedDomains);
        Assert.Equal("ru-RU", settings.DefaultLanguage);
    }
    #endregion METHOD_Build_ReturnsBoundValues

    #region METHOD_Build_UsesDefaults_ForEmptyConfig [DOMAIN(Config): SearXNG; TECH(Options): Defaults]
    /// <summary>
    /// [PURPOSE]: Verify defaults (string.Empty / 0 / []) apply when the settings class is
    /// instantiated without configuration. The defaults live on the class itself; an empty
    /// config section yields a default-constructed instance via the source-generated binder.
    /// </summary>
    [Fact]
    public void Build_UsesDefaults_ForEmptyConfig()
    {
        var settings = new SearXNGSettings();

        Assert.Equal(string.Empty, settings.BaseUrl);
        Assert.Equal(0, settings.MaxResults);
        Assert.Empty(settings.BlockedDomains);
        Assert.Equal(string.Empty, settings.DefaultLanguage);
    }
    #endregion METHOD_Build_UsesDefaults_ForEmptyConfig

    #region METHOD_ValidateOnStart_Passes_ForValidConfig [DOMAIN(Config): SearXNG; TECH(Options): Validate]
    /// <summary>
    /// [PURPOSE]: Verify a fully valid configuration resolves without throwing.
    /// </summary>
    [Fact]
    public void ValidateOnStart_Passes_ForValidConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SearXNG:BaseUrl"] = "http://searxng-instance:8080",
                ["SearXNG:MaxResults"] = "5",
                ["SearXNG:DefaultLanguage"] = "ru-RU",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<SearXNGSettings>()
            .Bind(config.GetSection("SearXNG"))
            .Validate(s => !string.IsNullOrWhiteSpace(s.BaseUrl) && Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out _), "SearXNG.BaseUrl must be a non-empty absolute URI")
            .Validate(s => s.MaxResults > 0, "SearXNG.MaxResults must be > 0")
            .Validate(s => !string.IsNullOrWhiteSpace(s.DefaultLanguage), "SearXNG.DefaultLanguage must be non-empty")
            .ValidateOnStart();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SearXNGSettings>>();

        var value = options.Value;
        Assert.Equal("http://searxng-instance:8080", value.BaseUrl);
        Assert.Equal(5, value.MaxResults);
        Assert.Equal("ru-RU", value.DefaultLanguage);
    }
    #endregion METHOD_ValidateOnStart_Passes_ForValidConfig

    #region METHOD_ValidateOnStart_Throws_ForInvalidConfig [DOMAIN(Config): SearXNG; TECH(Options): Validate]
    /// <summary>
    /// [PURPOSE]: Verify invalid configurations throw <see cref="OptionsValidationException"/>.
    /// Cases: empty BaseUrl, whitespace BaseUrl, MaxResults &lt;= 0, empty DefaultLanguage,
    /// invalid (non-absolute) URI.
    /// </summary>
    /// <param name="baseUrl">Base URL value to inject.</param>
    /// <param name="maxResults">MaxResults value to inject.</param>
    /// <param name="defaultLanguage">DefaultLanguage value to inject.</param>
    [Theory]
    [InlineData("", 5, "ru-RU")]            // empty BaseUrl
    [InlineData("   ", 5, "ru-RU")]          // whitespace BaseUrl
    [InlineData("http://x:8080", 0, "ru-RU")] // MaxResults <= 0
    [InlineData("http://x:8080", -3, "ru-RU")] // MaxResults < 0
    [InlineData("http://x:8080", 5, "")]      // empty DefaultLanguage
    [InlineData("http://x:8080", 5, "   ")]   // whitespace DefaultLanguage
    [InlineData("not-a-url", 5, "ru-RU")]     // invalid (relative) URI
    public void ValidateOnStart_Throws_ForInvalidConfig(string baseUrl, int maxResults, string defaultLanguage)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SearXNG:BaseUrl"] = baseUrl,
                ["SearXNG:MaxResults"] = maxResults.ToString(),
                ["SearXNG:DefaultLanguage"] = defaultLanguage,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<SearXNGSettings>()
            .Bind(config.GetSection("SearXNG"))
            .Validate(s => !string.IsNullOrWhiteSpace(s.BaseUrl) && Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out _), "SearXNG.BaseUrl must be a non-empty absolute URI")
            .Validate(s => s.MaxResults > 0, "SearXNG.MaxResults must be > 0")
            .Validate(s => !string.IsNullOrWhiteSpace(s.DefaultLanguage), "SearXNG.DefaultLanguage must be non-empty")
            .ValidateOnStart();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SearXNGSettings>>();

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }
    #endregion METHOD_ValidateOnStart_Throws_ForInvalidConfig
}
#endregion CLASS_SearXNGSettingsTests
