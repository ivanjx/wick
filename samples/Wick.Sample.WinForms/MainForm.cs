using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using Wick.Core;
using Wick.Sample.WinForms.Components;
using Wick.Sample.WinForms.State;

namespace Wick.Sample.WinForms;

/// <summary>
/// Native WinForms controls on the left, a <see cref="BlazorWebView"/> on the right, both over the same
/// <see cref="CounterState"/>. Blazor components re-render through Wick; the native labels are kept in sync by effects.
/// </summary>
public sealed class MainForm : Form
{
    private readonly CounterState _state;
    private readonly System.Threading.Timer _ticker;
    private readonly List<ReactiveEffect> _effects = [];

    private readonly Label _count = BigLabel("native-count");
    private readonly Label _countUpdates = UpdatesLabel();
    private readonly Button _toggle = new() { AutoSize = true, Name = "native-toggle" };
    private readonly Label _batch = BigLabel("native-batch");
    private readonly Label _batchUpdates = UpdatesLabel();

    public MainForm(IServiceProvider services)
    {
        _state = services.GetRequiredService<CounterState>();
        _ticker = new System.Threading.Timer(Tick);

        Text = "Wick on WinForms";
        ClientSize = new Size(1280, 800);
        Font = new Font("Segoe UI", 9.75f);
        StartPosition = FormStartPosition.CenterScreen;

        var increment = new Button { Text = "Increment", AutoSize = true, Name = "native-increment" };
        increment.Click += (_, _) => _state.Increment();
        _toggle.Click += (_, _) => _state.Ticking = !_state.Ticking;

        var bumpBoth = new Button { Text = "Update both", AutoSize = true, Name = "native-batch-button" };
        bumpBoth.Click += (_, _) => _state.BumpBoth();

        var native = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        native.Controls.AddRange(
        [
            new Label { Text = "Native controls", AutoSize = true, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold) },
            Hint("Each label below is set by its own Reactive.Effect, which re-runs only when the properties it read change."),
            Group("Counter", _count, _countUpdates, Row(increment, _toggle)),
            Group("Batch", _batch, _batchUpdates, Row(bumpBoth)),
            Hint("The buttons write the same CounterState the Blazor cards read: click either side and both update."),
        ]);

        var webView = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot/index.html",
            Services = services,
        };
        webView.RootComponents.Add<App>("#app");

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(native, 0, 0);
        layout.Controls.Add(webView, 1, 0);
        Controls.Add(layout);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _effects.Add(Bind(() => _state.Count, count => _count.Text = count.ToString(), _countUpdates));
        _effects.Add(Bind(() => _state.Ticking, ticking => _toggle.Text = ticking ? "Pause timer" : "Resume timer"));
        _effects.Add(Bind(() => (_state.Left, _state.Right), pair => _batch.Text = $"{pair.Left} / {pair.Right}", _batchUpdates));

        _ticker.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ticker.Dispose();
        foreach (var effect in _effects)
        {
            effect.Dispose();
        }

        base.OnFormClosed(e);
    }

    // The read runs inside the effect, so Wick tracks it; re-runs happen on the thread pool, so the
    // control update is marshalled to the UI thread.
    private ReactiveEffect Bind<T>(Func<T> read, Action<T> apply, Label? updates = null)
    {
        var runs = 0;
        return Reactive.Effect(() =>
        {
            var value = read();
            BeginInvoke(() =>
            {
                apply(value);
                updates?.Text = $"Effect runs: {++runs}";
            });
        });
    }

    // Writes from thread-pool timer callbacks: the Blazor cards and the effects above pick them up without marshalling here.
    private void Tick(object? _)
    {
        if (_state.Ticking)
        {
            _state.Increment();
        }
    }

    private static Label BigLabel(string name) => new()
    {
        Name = name,
        AutoSize = true,
        Font = new Font("Segoe UI", 24f, FontStyle.Bold),
    };

    private static Label UpdatesLabel() => new() { AutoSize = true, ForeColor = Color.DimGray };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(280, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(3, 6, 3, 6),
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange(controls);
        return row;
    }

    private static GroupBox Group(string title, params Control[] controls)
    {
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        content.Controls.AddRange(controls);

        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            MinimumSize = new Size(280, 0),
            Padding = new Padding(8),
        };
        group.Controls.Add(content);
        return group;
    }
}
