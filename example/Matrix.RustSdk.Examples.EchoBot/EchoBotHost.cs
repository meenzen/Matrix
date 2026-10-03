using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Matrix.RustSdk.Examples.EchoBot;

/// <summary>
/// Builds the host of the echo bot. <c>Program.cs</c> runs it, the tests start the same host in-process.
/// </summary>
public static class EchoBotHost
{
    /// <summary>
    /// Creates the host with the default configuration sources: <c>appsettings.json</c>, environment variables
    /// (<c>EchoBot__Password</c>) and command line arguments (<c>--EchoBot:Password=...</c>).
    /// </summary>
    public static IHost Create(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder
            .Services.AddOptions<EchoBotOptions>()
            .Bind(builder.Configuration.GetSection(EchoBotOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddHostedService<EchoBotWorker>();

        return builder.Build();
    }
}
