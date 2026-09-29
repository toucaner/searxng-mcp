using Serilog;
using Serilog.Sinks.Syslog;

namespace McpWebSearchService;

/// <summary>
/// [PURPOSE]: Dedicated Serilog logger that forwards Information-level audit events
/// (health status + /mcp request logging) to the Wazuh manager via syslog (UDP).
/// Host/port/appName are configurable; call Configure(...) from Program.cs before use.
/// </summary>
public static class WazuhAudit
{
    private static Serilog.Core.Logger? _logger;

    /// <summary>Initializes the audit logger from configuration values.</summary>
    public static void Configure(string host, int port, string appName)
    {
        _logger?.Dispose();
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.UdpSyslog(host, port, appName, SyslogFormat.RFC5424)
            .CreateLogger();
    }

    /// <summary>Logs a health-check result (e.g. "MCP-HEALTH mcp-web-search OK").</summary>
    public static void Health(string service, string status) =>
        _logger?.Information("MCP-HEALTH {Service} {Status}", service, status);

    /// <summary>Logs an incoming /mcp request.</summary>
    public static void McpRequest(string method, int statusCode, string remoteIp) =>
        _logger?.Information("MCP-REQ method={Method} status={StatusCode} src={RemoteIp}", method, statusCode, remoteIp);
}
