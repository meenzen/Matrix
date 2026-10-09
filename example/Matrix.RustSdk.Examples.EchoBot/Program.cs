using Matrix.RustSdk;
using Matrix.RustSdk.Examples.EchoBot;
using Microsoft.Extensions.Hosting;

// once per process, before the bot creates its client: the SDK logs its warnings and errors to the console
MatrixSdk.Initialize(new MatrixSdkOptions { LogToConsole = true });

using IHost host = EchoBotHost.Create(args);
await host.RunAsync();
