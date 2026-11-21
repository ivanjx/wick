using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Wick.Sample.WinForms.State;

namespace Wick.Sample.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var services = new ServiceCollection();
        services.AddWindowsFormsBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        // One window, one user: the Blazor components and the native controls share these instances.
        services.AddSingleton<CounterState>();
        services.AddSingleton<LogState>();
        services.AddSingleton<ClassicFeedState>();
        services.AddSingleton<FeedState>();

        using var provider = services.BuildServiceProvider();
        Application.Run(new MainForm(provider));
    }
}
