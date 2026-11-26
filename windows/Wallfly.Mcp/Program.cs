using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wallfly.Mcp;

// wallfly-mcp [--library <path>]: local, read-only MCP server over stdio (port of WallflyMCPMain.swift).
// Without --library it reads the library the Wallfly app uses (LibraryPointer, %LOCALAPPDATA%\Wallfly\library.json),
// resolved again on every call, so moving the library in the app needs no client change.
if (args is ["--help"])
{
    Console.WriteLine(Messages.Usage);
    return 0;
}
string? library = null;
if (args is ["--library", var path] && !string.IsNullOrWhiteSpace(path)) library = path;
else if (args.Length != 0)
{
    Console.Error.WriteLine(Messages.StartFailed);
    return 1;
}

try
{
    // No default configuration sources: nothing is read from or watched in the working directory, and the
    // only log provider is the stderr console configured below.
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        DisableDefaults = true,
        ApplicationName = "wallfly-mcp",
        ContentRootPath = AppContext.BaseDirectory
    });
    WallflyMcpServer.ConfigureLogging(builder.Logging);
    builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
    builder.Services.AddWallflyMcp(library).WithStdioServerTransport();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(Messages.StartFailed);
    return 1;
}
