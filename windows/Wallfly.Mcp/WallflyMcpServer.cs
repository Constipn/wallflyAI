using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Wallfly.Mcp;

/// <summary>Composition of the server, shared by <c>wallfly-mcp.exe</c> (stdio) and the in-process tests (streams).</summary>
public static class WallflyMcpServer
{
    public const string ServerName = "Wallfly";
    public const string ServerVersion = "1.0.0";

    /// <summary>
    /// Registers the library reader and the three tools. The caller adds the transport, e.g.
    /// <c>services.AddWallflyMcp(path).WithStdioServerTransport()</c>.
    /// </summary>
    public static IMcpServerBuilder AddWallflyMcp(this IServiceCollection services, string? libraryPath = null)
    {
        services.AddSingleton(provider => new LibraryReader(libraryPath, provider.GetService<ILogger<LibraryReader>>()));
        services.AddSingleton<MeetingTools>();
        services.AddHostedService<LibraryWarmup>();
        services.PostConfigure<McpServerOptions>(options => ToolDefinitions.Apply(options.ToolCollection));
        return services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion };
                options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
            })
            .WithTools<WallflyTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                // Like the macOS helper, every failure is a tool result with one fixed sentence: unknown tool
                // names and SDK-level errors included, never an exception text with internals.
                try
                {
                    var result = await next(context, cancellationToken);
                    return result.IsError == true && !Messages.IsKnown((result.Content.FirstOrDefault() as TextContentBlock)?.Text)
                        ? WallflyTools.Failure(ReadFailure.InvalidArguments)
                        : result;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return error is ReadException read ? WallflyTools.Failure(read.Failure) : WallflyTools.Failure(ReadFailure.InvalidArguments);
                }
            }));
    }

    /// <summary>stdout is the protocol: every log line goes to stderr, and only warnings of the host and SDK.</summary>
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.ColorBehavior = LoggerColorBehavior.Disabled;
            options.TimestampFormat = "HH:mm:ss ";
        });
        logging.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
    }

    /// <summary>Parses the library once in the background so the first tool call is fast. Never creates anything.</summary>
    private sealed class LibraryWarmup(LibraryReader library, ILogger<LibraryWarmup> logger) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
        {
            try
            {
                var snapshot = library.Snapshot();
                logger.LogInformation("Library ready: {Count} meeting(s), {Skipped} skipped.", snapshot.Meetings.Count, snapshot.Skipped);
            }
            catch (ReadException)
            {
                logger.LogWarning("The meeting library is unavailable.");
            }
        }, stoppingToken);
    }
}
