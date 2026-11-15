using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Wick.E2E.Tests;

/// <summary>
/// Hosts <c>Wick.E2E.Host</c> on Kestrel at a free port and launches the installed browser once for all tests:
/// Edge on Windows, Chrome elsewhere, so no Playwright browser download is needed.
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public Uri BaseAddress { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();

        var addresses = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        BaseAddress = new Uri(addresses.Addresses.First());

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = OperatingSystem.IsWindows() ? "msedge" : "chrome",
        });
    }

    /// <summary>Opens a page in a new browser context, so each page gets its own circuit and storage.</summary>
    public async Task<IPage> OpenAsync(string path)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions { BaseURL = BaseAddress.ToString() });
        var page = await context.NewPageAsync();

        await page.GotoAsync(path);

        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
        await _factory.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>
{
    public const string Name = "Browser";
}
