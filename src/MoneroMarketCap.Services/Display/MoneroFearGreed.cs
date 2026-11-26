namespace MoneroMarketCap.Services.Display;

/// <summary>
/// A fear and greed reading for Monero, 0 (extreme fear) to 100 (extreme greed), worked out
/// from daily prices alone. Four parts, each scored 0 to 100 against Monero's own last year:
///
///   • Momentum (40%): the 30-day price change, ranked among the 30-day changes of the last
///     365 days. A strong month by Monero's own standards reads as greed.
///   • Distance from the high (20%): today's price against the highest of the last 90 days.
///     At the high is 100; 40% or more below it is 0.
///   • Volatility (20%): how jumpy the last 30 days were, ranked among the last 365 days and
///     turned upside down. Unusually wild swings read as fear.
///   • Monero against Bitcoin (20%): the 30-day change of the XMR/BTC price, ranked the same
///     way. Monero gaining on Bitcoin reads as greed.
///
/// It needs about 13 months of daily prices before the first reading. It is this site's own
/// gauge, not an industry standard, and it is not a prediction.
/// </summary>
public static class MoneroFearGreed
{
    public const int Window = 30;
    public const int HighWindow = 90;
    public const int RankWindow = 365;

    /// <summary>Days of history needed before the first reading.</summary>
    public const int MinDays = RankWindow + Window + 1;

    /// <summary>
    /// Readings for every date that has enough history. Both series must be daily closes in
    /// date order; dates missing from the Bitcoin series simply drop that part of the score.
    /// </summary>
    public static IReadOnlyList<(DateTime Date, int Value)> Compute(
        IReadOnlyList<(DateTime Date, decimal Price)> xmr,
        IReadOnlyList<(DateTime Date, decimal Price)> btc)
    {
        var result = new List<(DateTime, int)>();
        var days = xmr.Where(p => p.Price > 0).OrderBy(p => p.Date).ToList();
        if (days.Count < MinDays)
        {
            return result;
        }

        var btcByDate = btc.Where(p => p.Price > 0).GroupBy(p => p.Date.Date).ToDictionary(g => g.Key, g => (double)g.Last().Price);
        var price = days.Select(d => (double)d.Price).ToArray();
        int n = price.Length;

        // XMR priced in BTC (NaN where Bitcoin has no price that day).
        var ratio = new double[n];
        for (int i = 0; i < n; i++)
        {
            ratio[i] = btcByDate.TryGetValue(days[i].Date.Date, out var b) ? price[i] / b : double.NaN;
        }

        // Per-day series the ranks are taken over.
        var change30 = new double[n];
        var ratioChange30 = new double[n];
        var vol30 = new double[n];
        for (int i = 0; i < n; i++)
        {
            change30[i] = i >= Window ? (price[i] / price[i - Window]) - 1 : double.NaN;
            ratioChange30[i] = i >= Window && !double.IsNaN(ratio[i]) && !double.IsNaN(ratio[i - Window])
                ? (ratio[i] / ratio[i - Window]) - 1
                : double.NaN;
            if (i >= Window)
            {
                double sum = 0, sumSq = 0;
                for (int k = i - Window + 1; k <= i; k++)
                {
                    var r = Math.Log(price[k] / price[k - 1]);
                    sum += r;
                    sumSq += r * r;
                }

                var mean = sum / Window;
                vol30[i] = Math.Sqrt(Math.Max(0, (sumSq / Window) - (mean * mean)));
            }
            else
            {
                vol30[i] = double.NaN;
            }
        }

        for (int i = MinDays - 1; i < n; i++)
        {
            var momentum = PercentRank(change30, i);
            var volatility = 100 - PercentRank(vol30, i);

            double high = 0;
            for (int k = Math.Max(0, i - HighWindow + 1); k <= i; k++)
            {
                high = Math.Max(high, price[k]);
            }

            var fromHigh = Math.Clamp((((price[i] / high) - 0.6) / 0.4) * 100, 0, 100);

            double total, weight;
            total = (momentum * 0.40) + (fromHigh * 0.20) + (volatility * 0.20);
            weight = 0.80;
            if (!double.IsNaN(ratioChange30[i]))
            {
                total += PercentRank(ratioChange30, i) * 0.20;
                weight += 0.20;
            }

            result.Add((days[i].Date.Date, (int)Math.Round(Math.Clamp(total / weight, 0, 100))));
        }

        return result;
    }

    /// <summary>The usual five bands.</summary>
    public static string Label(int value) => value switch
    {
        < 25 => "Extreme Fear",
        < 46 => "Fear",
        < 55 => "Neutral",
        < 76 => "Greed",
        _ => "Extreme Greed",
    };

    // Where series[i] stands among the last RankWindow values up to and including i: 0 = lowest, 100 = highest.
    private static double PercentRank(double[] series, int i)
    {
        var v = series[i];
        int below = 0, count = 0;
        for (int k = Math.Max(0, i - RankWindow + 1); k <= i; k++)
        {
            if (double.IsNaN(series[k]))
            {
                continue;
            }

            count++;
            if (series[k] < v)
            {
                below++;
            }
        }

        return count <= 1 ? 50 : below * 100.0 / (count - 1);
    }
}
