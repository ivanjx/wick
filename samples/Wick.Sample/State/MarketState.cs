using Wick.Core;

namespace Wick.Sample.State;

[ReactiveState]
public sealed partial class Ticker
{
    // Init-only: never changes after construction, so it does not need to be reactive.
    public required string Symbol { get; init; }

    public partial double Price { get; set; }

    public partial double Change { get; set; }
}

/// <summary>Singleton market data shared by every circuit, written only by <see cref="MarketFeed"/>.</summary>
[ReactiveState]
public sealed partial class MarketState
{
    // Singleton for the lifetime of the app, so the derived values are never disposed.
    private readonly Derived<double> _averagePrice;
    private readonly Derived<string> _mood;

    public MarketState()
    {
        _averagePrice = Reactive.Derived(() => Tickers.Average(ticker => ticker.Price));

        _mood = Reactive.Derived(() =>
        {
            var advancing = Tickers.Count(ticker => ticker.Change > 0);
            var declining = Tickers.Count(ticker => ticker.Change < 0);

            return advancing > declining ? "Bullish" : advancing < declining ? "Bearish" : "Flat";
        });
    }

    public IReadOnlyList<Ticker> Tickers { get; } =
    [
        new() { Symbol = "AAPL", Price = 190 },
        new() { Symbol = "MSFT", Price = 420 },
        new() { Symbol = "NVDA", Price = 880 },
        new() { Symbol = "AMZN", Price = 180 },
        new() { Symbol = "GOOG", Price = 165 },
        new() { Symbol = "META", Price = 500 },
        new() { Symbol = "TSLA", Price = 175 },
        new() { Symbol = "AVGO", Price = 1300 },
        new() { Symbol = "ORCL", Price = 125 },
        new() { Symbol = "ADBE", Price = 480 },
        new() { Symbol = "CRM", Price = 270 },
        new() { Symbol = "AMD", Price = 160 },
        new() { Symbol = "INTC", Price = 30 },
        new() { Symbol = "CSCO", Price = 48 },
        new() { Symbol = "IBM", Price = 185 },
        new() { Symbol = "QCOM", Price = 170 },
        new() { Symbol = "TXN", Price = 175 },
        new() { Symbol = "NFLX", Price = 620 },
        new() { Symbol = "PYPL", Price = 65 },
        new() { Symbol = "UBER", Price = 70 },
        new() { Symbol = "SHOP", Price = 75 },
        new() { Symbol = "NOW", Price = 760 },
        new() { Symbol = "INTU", Price = 640 },
        new() { Symbol = "SNOW", Price = 160 },
        new() { Symbol = "PLTR", Price = 24 },
        new() { Symbol = "JPM", Price = 195 },
        new() { Symbol = "BAC", Price = 37 },
        new() { Symbol = "WFC", Price = 58 },
        new() { Symbol = "GS", Price = 450 },
        new() { Symbol = "MS", Price = 95 },
        new() { Symbol = "V", Price = 275 },
        new() { Symbol = "MA", Price = 460 },
        new() { Symbol = "AXP", Price = 230 },
        new() { Symbol = "BRK", Price = 410 },
        new() { Symbol = "JNJ", Price = 155 },
        new() { Symbol = "PFE", Price = 28 },
        new() { Symbol = "MRK", Price = 128 },
        new() { Symbol = "LLY", Price = 770 },
        new() { Symbol = "UNH", Price = 490 },
        new() { Symbol = "ABBV", Price = 165 },
        new() { Symbol = "WMT", Price = 60 },
        new() { Symbol = "COST", Price = 720 },
        new() { Symbol = "KO", Price = 60 },
        new() { Symbol = "PEP", Price = 170 },
        new() { Symbol = "MCD", Price = 270 },
        new() { Symbol = "NKE", Price = 95 },
        new() { Symbol = "DIS", Price = 110 },
        new() { Symbol = "XOM", Price = 115 },
        new() { Symbol = "CVX", Price = 155 },
        new() { Symbol = "BA", Price = 185 },
    ];

    public partial long Ticks { get; set; }

    public partial long PriceUpdates { get; set; }

    // Computed once per tick on the feed thread, however many circuits read it.
    public double AveragePrice => _averagePrice.Value;

    // Recomputed every tick too, but readers re-render only when the result flips.
    public string Mood => _mood.Value;

    /// <summary>Moves one to five random tickers.</summary>
    public void Tick(Random random)
    {
        var moves = random.Next(1, 6);
        for (var i = 0; i < moves; i++)
        {
            var ticker = Tickers[random.Next(Tickers.Count)];
            var change = Math.Round(ticker.Price * (random.NextDouble() - 0.5) * 0.01, 2);

            ticker.Price = Math.Round(ticker.Price + change, 2);
            ticker.Change = change;
        }

        Ticks++;
        PriceUpdates += moves;
    }
}

/// <summary>Writes market data from a hosted service, outside any circuit or dispatcher.</summary>
public sealed class MarketFeed(MarketState market) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // One batch per tick: a component that reads several tickers (the summary) is
                // invalidated once per tick instead of once per changed price.
                Reactive.Batch(() => market.Tick(Random.Shared));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
