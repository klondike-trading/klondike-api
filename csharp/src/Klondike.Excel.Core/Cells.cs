using System.Globalization;

namespace Klondike.Excel;

/// <summary>The error values of Excel a feed can give, without depending on Excel.</summary>
public enum CellError
{
    /// <summary><c>#N/A</c>: the value does not exist (no bid on this venue, for instance).</summary>
    NotAvailable,
    /// <summary><c>#GETTING_DATA</c>: waiting for the first value.</summary>
    GettingData,
}

/// <summary>Conversions to the values of a cell: double, string, bool.</summary>
public static class Cells
{
    /// <summary>The message of an error, shown in the cell. Text, so that it is read and not computed with.</summary>
    public static string Error(string message) => message.StartsWith("klondike: ", StringComparison.Ordinal) ? message : "klondike: " + message;

    /// <summary>Excel date (days since 1899-12-30), in UTC.</summary>
    public static double Date(DateTimeOffset time) => time.UtcDateTime.ToOADate();

    /// <summary>Excel date of a timestamp in milliseconds.</summary>
    public static double Date(long milliseconds) => Date(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));

    /// <summary>A scalar for a cell: numbers as doubles, a missing value as <paramref name="missing"/>.</summary>
    public static object Value(object? value, object missing) => value switch
    {
        null => missing,
        double d => double.IsNaN(d) || double.IsInfinity(d) ? missing : d,
        int i => (double)i,
        long l => (double)l,
        Side.Buy => "buy",
        Side.Sell => "sell",
        Side => "unknown",
        string or bool => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? missing,
    };
}
