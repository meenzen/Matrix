using Matrix.RustSdk.Examples.TuiClient;
using Terminal.Gui.App;

LoginOptions options = LoginOptions.Parse(args);

using IApplication app = Application.Create().Init();
await TuiClient.RunAsync(app, options);
