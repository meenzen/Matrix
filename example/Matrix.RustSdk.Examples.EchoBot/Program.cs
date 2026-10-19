using Matrix.RustSdk;
using Matrix.RustSdk.Examples.EchoBot;
using Microsoft.Extensions.Hosting;

// once per process, before the bot creates its client. The SDK logs every request, so its logs go to files in the logs
// directory instead of mixing with the logs of the bot on the console
MatrixSdk.Initialize(new MatrixSdkOptions { LogDirectory = "logs" });

using IHost host = EchoBotHost.Create(args);
await host.RunAsync();
