using Matrix.RustSdk.Examples.EchoBot;
using Microsoft.Extensions.Hosting;

using IHost host = EchoBotHost.Create(args);
await host.RunAsync();
