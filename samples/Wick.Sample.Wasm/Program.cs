using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Wick.Sample.Wasm;
using Wick.Sample.Wasm.State;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// In WebAssembly a singleton lives as long as the browser tab: one state per user.
builder.Services.AddSingleton<CounterState>();
builder.Services.AddSingleton<LogState>();

await builder.Build().RunAsync();
