using Matrix.RustSdk;
using Matrix.RustSdk.Examples.TuiClient;
using Terminal.Gui.App;

// once per process, without logs: the console belongs to the UI
MatrixSdk.Initialize();

LoginOptions options = LoginOptions.Parse(args);

using IApplication app = Application.Create().Init();
await TuiClient.RunAsync(app, options);
