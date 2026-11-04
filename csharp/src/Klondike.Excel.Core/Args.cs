namespace Klondike.Excel;

/// <summary>
/// The arguments of the functions. Excel gives a double, a string, a bool, a range as an array; the add-in
/// gives null for a missing or empty one, in a range too. An invalid value is an
/// <see cref="ArgumentException"/>, whose message is shown in the cell.
/// </summary>
public static class Args
{
    /// <summary>A required symbol.</summary>
    public static string Symbol(object? value)
    {
        var symbol = Text(value, "symbol", null);
        return string.IsNullOrEmpty(symbol) ? throw new ArgumentException("symbol is required, for instance \"BTC/USDT\"") : symbol;
    }

    /// <summary>Optional text.</summary>
    public static string? Text(object? value, string name, string? fallback)
    {
        switch (value)
        {
            case null:
                return fallback;
            case string s:
                s = s.Trim();
                return s.Length > 0 ? s : fallback;
            default:
                throw new ArgumentException($"{name} must be text");
        }
    }

    /// <summary>Optional whole number between two bounds.</summary>
    public static int Int(object? value, string name, int fallback, int min, int max)
    {
        if (value is null || value is string { Length: 0 })
        {
            return fallback;
        }
        if (value is not double d || d != Math.Floor(d) || d < min || d > max)
        {
            throw new ArgumentException($"{name} must be a whole number from {min} to {max}");
        }
        return (int)d;
    }

    /// <summary>A required number.</summary>
    public static double Number(object? value, string name) =>
        value is double d ? d : throw new ArgumentException($"{name} must be a number");

    /// <summary>Optional boolean.</summary>
    public static bool Bool(object? value, string name, bool fallback) => value switch
    {
        null or string { Length: 0 } => fallback,
        bool b => b,
        double d => d != 0,
        _ => throw new ArgumentException($"{name} must be TRUE or FALSE"),
    };

    /// <summary>
    /// Venues: "binance,okx" or a range of cells holding them. Null for all venues. The same venues give the
    /// same text, whatever their order: Excel subscribes once per distinct arguments.
    /// </summary>
    public static string? Venues(object? value)
    {
        IEnumerable<object?> cells = value switch
        {
            null => [],
            object?[,] range => range.Cast<object?>(),
            _ => [value],
        };
        var venues = new List<string>();
        foreach (var cell in cells)
        {
            if (cell is string text)
            {
                venues.Add(text);
            }
            else if (cell is not null)
            {
                throw new ArgumentException("venues must be text, for instance \"binance,okx\"");
            }
        }
        var canonical = MarketHub.CanonicalVenues(string.Join(',', venues));
        return canonical.Length > 0 ? canonical : null;
    }
}
