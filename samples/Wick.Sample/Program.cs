using Wick.Sample.Components;
using Wick.Sample.State;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Per-circuit state: every browser tab gets its own instance.
builder.Services.AddScoped<CounterState>();
builder.Services.AddScoped<GridState>();
builder.Services.AddScoped<ProfileState>();
builder.Services.AddScoped<TodoState>();
builder.Services.AddScoped<CartState>();
builder.Services.AddScoped<SearchState>();
builder.Services.AddScoped<DraftState>();

// A fake remote API for the search page.
builder.Services.AddSingleton<LanguageCatalog>();

// App-wide state shared by every circuit and written from a background service.
builder.Services.AddSingleton<MarketState>();
builder.Services.AddHostedService<MarketFeed>();

var app = builder.Build();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
