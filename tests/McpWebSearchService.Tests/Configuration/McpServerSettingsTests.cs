#region MODULE_CONTRACT [DOMAIN(Config): McpServer; CONCEPT(Test): Settings binding + validation; TECH(xUnit): Fact/Theory]
/**
 * [GREP_SUMMARY]: McpServerSettingsTests, xunit, test, mcpserver, settings, binding, validation, defaults, OptionsValidationException
 * [STRUCTURE]: > Config(dict) -> o Configure<McpServerSettings> -> + IOptions.Value -> = Assert(fields) | > Invalid -> o ValidateOnStart -> + OptionsValidationException
 *
 * <summary>
 * [PURPOSE]: Verify McpServerSettings configuration binding, defaults, and validation rules
 * (DevelopmentPlan.md §3 AC2/AC6/AC8). Covers: value binding (Name, Version), defaults under empty
 * config, positive validation, and negative validation (empty Name, empty Version).
 * </summary>
 * <remarks>
 * [INVARIANTS]: Tests use ConfigurationBuilder with in-memory dictionary (no real appsettings.json).
 * Validation is exercised via OptionsBuilder.Validate + ValidateOnStart through a ServiceProvider,
 * which throws OptionsValidationException when invalid config is accessed.
 * [RATIONALE]: Q: Why resolve IOptions&lt;T&gt;.Value from a built provider instead of calling the
/// Validate lambda directly? A: ValidateOnStart wires the validators into the Options pipeline;
/// resolving .Value triggers the same validation path the host uses at startup — the closest
/// unit-level equivalent to the runtime behaviour verified in AC5.
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

#region CLASS_McpServerSettingsTests [DOMAIN(Config): McpServer; CONCEPT(Test): Binding + validation]
/// <summary>
/// [PURPOSE]: Unit tests for <see cref="McpServerSettings"/> binding, defaults, and validation.
/// </summary>
public class McpServerSettingsTests
{
    #region METHOD_Build_ReturnsBoundValues [DOMAIN(Config): McpServer; TECH(Options): Bind]
    /// <summary>
    /// [PURPOSE]: Verify Name and Version bind correctly from an in-memory configuration.
    /// </summary>
    [Fact]
    public void Build_ReturnsBoundValues_ForNameAndVersion()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServer:Name"] = "web-search-mcp",
                ["McpServer:Version"] = "1.0.0",
            })
            .Build();

        var settings = config.GetSection("McpServer").Get<McpServerSettings>();

        Assert.NotNull(settings);
        Assert.Equal("web-search-mcp", settings!.Name);
        Assert.Equal("1.0.0", settings.Version);
    }
    #endregion METHOD_Build_ReturnsBoundValues

    #region METHOD_Build_UsesDefaults_ForEmptyConfig [DOMAIN(Config): McpServer; TECH(Options): Defaults]
    /// <summary>
    /// [PURPOSE]: Verify defaults (string.Empty) apply when the settings class is instantiated
    /// without configuration. The defaults live on the class itself; an empty config section
    /// yields a default-constructed instance via the source-generated binder.
    /// </summary>
    [Fact]
    public void Build_UsesDefaults_ForEmptyConfig()
    {
        var settings = new McpServerSettings();

        Assert.Equal(string.Empty, settings.Name);
        Assert.Equal(string.Empty, settings.Version);
    }
    #endregion METHOD_Build_UsesDefaults_ForEmptyConfig

    #region METHOD_ValidateOnStart_Passes_ForValidConfig [DOMAIN(Config): McpServer; TECH(Options): Validate]
    /// <summary>
    /// [PURPOSE]: Verify a fully valid configuration resolves without throwing.
    /// </summary>
    [Fact]
    public void ValidateOnStart_Passes_ForValidConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServer:Name"] = "web-search-mcp",
                ["McpServer:Version"] = "1.0.0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<McpServerSettings>()
            .Bind(config.GetSection("McpServer"))
            .Validate(s => !string.IsNullOrWhiteSpace(s.Name), "McpServer.Name must be non-empty")
            .Validate(s => !string.IsNullOrWhiteSpace(s.Version), "McpServer.Version must be non-empty")
            .ValidateOnStart();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerSettings>>();

        var value = options.Value;
        Assert.Equal("web-search-mcp", value.Name);
        Assert.Equal("1.0.0", value.Version);
    }
    #endregion METHOD_ValidateOnStart_Passes_ForValidConfig

    #region METHOD_ValidateOnStart_Throws_ForInvalidConfig [DOMAIN(Config): McpServer; TECH(Options): Validate]
    /// <summary>
    /// [PURPOSE]: Verify invalid configurations throw <see cref="OptionsValidationException"/>.
    /// Cases: empty Name, whitespace Name, empty Version, whitespace Version.
    /// </summary>
    /// <param name="name">Name value to inject.</param>
    /// <param name="version">Version value to inject.</param>
    [Theory]
    [InlineData("", "1.0.0")]      // empty Name
    [InlineData("   ", "1.0.0")]    // whitespace Name
    [InlineData("web-search-mcp", "")]   // empty Version
    [InlineData("web-search-mcp", "   ")] // whitespace Version
    public void ValidateOnStart_Throws_ForInvalidConfig(string name, string version)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["McpServer:Name"] = name,
                ["McpServer:Version"] = version,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<McpServerSettings>()
            .Bind(config.GetSection("McpServer"))
            .Validate(s => !string.IsNullOrWhiteSpace(s.Name), "McpServer.Name must be non-empty")
            .Validate(s => !string.IsNullOrWhiteSpace(s.Version), "McpServer.Version must be non-empty")
            .ValidateOnStart();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<McpServerSettings>>();

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }
    #endregion METHOD_ValidateOnStart_Throws_ForInvalidConfig
}
#endregion CLASS_McpServerSettingsTests
