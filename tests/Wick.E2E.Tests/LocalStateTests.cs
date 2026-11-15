namespace Wick.E2E.Tests;

[Collection(BrowserCollection.Name)]
public sealed class LocalStateTests(BrowserFixture browser)
{
    private const int Ticks = 5;

    [Theory]
    [InlineData("server")]
    [InlineData("wasm")]
    public async Task A_js_setInterval_writing_a_ReactiveValue_re_renders_the_component(string mode)
    {
        var page = await browser.OpenAsync($"/js-interval/{mode}");

        await WaitForNumberAsync(page, "#wick-ticks", Ticks);

        // The plain component was called just as often but, with no StateHasChanged, never re-rendered.
        await page.WaitForFunctionAsync($"() => (window.wickE2E.calls.plain ?? 0) >= {Ticks}");
        Assert.Equal("0", await page.Locator("#plain-ticks").TextContentAsync());

        await page.Context.CloseAsync();
    }

    [Theory]
    [InlineData("server")]
    [InlineData("wasm")]
    public async Task A_dotnet_timer_writing_a_code_behind_partial_property_re_renders_the_component(string mode)
    {
        var page = await browser.OpenAsync($"/timer/{mode}");

        await WaitForNumberAsync(page, "#timer-ticks", Ticks);

        await page.Context.CloseAsync();
    }

    [Fact]
    public async Task A_click_in_one_tab_re_renders_another_tab_reading_the_same_singleton_state()
    {
        var reader = await browser.OpenAsync("/shared-counter");
        var writer = await browser.OpenAsync("/shared-counter");

        var count = int.Parse(await ReadAfterRenderAsync(reader, "#count"));
        await ReadAfterRenderAsync(writer, "#count");

        await writer.ClickAsync("#increment");

        await WaitForNumberAsync(reader, "#count", count + 1);

        await reader.Context.CloseAsync();
        await writer.Context.CloseAsync();
    }

    private static Task WaitForNumberAsync(IPage page, string selector, int atLeast)
        => page.WaitForFunctionAsync(
            "([selector, atLeast]) => Number(document.querySelector(selector)?.textContent) >= atLeast",
            new object[] { selector, atLeast });

    // Prerendering is off, so the element appears with the first interactive render.
    private static async Task<string> ReadAfterRenderAsync(IPage page, string selector)
        => await (await page.WaitForSelectorAsync(selector))!.TextContentAsync() ?? "";
}
