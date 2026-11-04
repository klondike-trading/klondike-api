using Klondike.Excel;

namespace Klondike.Tests;

public class CellsTests
{
    [Fact]
    public void DatesAreExcelSerialsInUtc()
    {
        Assert.Equal(new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc).ToOADate(), Cells.Date(new DateTimeOffset(2026, 11, 4, 13, 0, 0, TimeSpan.FromHours(1))));
        Assert.Equal(25569d, Cells.Date(0)); // 1970-01-01 in Excel
    }

    [Fact]
    public void ValuesAreThoseExcelTakes()
    {
        Assert.Equal(3d, Cells.Value(3, ""));
        Assert.Equal(3d, Cells.Value(3L, ""));
        Assert.Equal("x", Cells.Value("x", ""));
        Assert.Equal(true, Cells.Value(true, ""));
        Assert.Equal("buy", Cells.Value(Side.Buy, ""));
        Assert.Equal("sell", Cells.Value(Side.Sell, ""));
        Assert.Equal("unknown", Cells.Value(Side.Unknown, ""));
        Assert.Equal("", Cells.Value(null, ""));
        Assert.Equal(CellError.NotAvailable, Cells.Value(null, CellError.NotAvailable));
        Assert.Equal("", Cells.Value(double.NaN, ""));
        Assert.Equal("", Cells.Value(double.PositiveInfinity, ""));
    }

    [Fact]
    public void ErrorsAreNotPrefixedTwice()
    {
        Assert.Equal("klondike: x", Cells.Error("x"));
        Assert.Equal("klondike: x", Cells.Error("klondike: x"));
    }

    [Fact]
    public void Text()
    {
        Assert.Equal("a", Args.Text("  a ", "n", null));
        Assert.Equal("d", Args.Text(null, "n", "d"));
        Assert.Equal("d", Args.Text("  ", "n", "d"));
        Assert.Contains("n must be text", Assert.Throws<ArgumentException>(() => Args.Text(1d, "n", null)).Message);
        Assert.Equal("BTC/USDT", Args.Symbol(" BTC/USDT"));
        Assert.Equal("kPEPE/USDC", Args.Symbol("kPEPE/USDC")); // the case of a symbol matters
        Assert.Throws<ArgumentException>(() => Args.Symbol(null));
        Assert.Throws<ArgumentException>(() => Args.Symbol(""));
    }

    [Fact]
    public void Numbers()
    {
        Assert.Equal(5, Args.Int(null, "n", 5, 1, 10));
        Assert.Equal(5, Args.Int("", "n", 5, 1, 10));
        Assert.Equal(7, Args.Int(7d, "n", 5, 1, 10));
        Assert.Throws<ArgumentException>(() => Args.Int(0d, "n", 5, 1, 10));
        Assert.Throws<ArgumentException>(() => Args.Int(11d, "n", 5, 1, 10));
        Assert.Throws<ArgumentException>(() => Args.Int(1.5, "n", 5, 1, 10));
        Assert.Throws<ArgumentException>(() => Args.Int("x", "n", 5, 1, 10));
        Assert.Equal(2d, Args.Number(2d, "n"));
        Assert.Throws<ArgumentException>(() => Args.Number(null, "n"));
    }

    [Fact]
    public void Booleans()
    {
        Assert.True(Args.Bool(null, "n", true));
        Assert.False(Args.Bool(false, "n", true));
        Assert.False(Args.Bool(0d, "n", true));
        Assert.True(Args.Bool(1d, "n", false));
        Assert.Throws<ArgumentException>(() => Args.Bool("maybe", "n", false));
    }

    [Fact]
    public void Venues()
    {
        Assert.Null(Args.Venues(null));
        Assert.Null(Args.Venues(""));
        Assert.Null(Args.Venues(new object?[,] { { null }, { null } }));
        Assert.Equal("binance,okx", Args.Venues("OKX, binance"));
        Assert.Equal("binance,kraken,okx", Args.Venues(new object?[,] { { "okx,binance" }, { null }, { "kraken" } }));
        Assert.Throws<ArgumentException>(() => Args.Venues(1d));
        Assert.Throws<ArgumentException>(() => Args.Venues(new object?[,] { { 1d } }));
    }

    [Fact]
    public void AnEmptyTableWithoutHeadersIsStillACell()
    {
        var table = Tables.Trades([], 5, false);
        Assert.Equal("", Assert.Single(table.Cast<object>()));
        Assert.Equal(1, Tables.Trades([], 5, true).GetLength(0));
    }

    [Fact]
    public void EverySectionOfTheOverviewHasATable()
    {
        var overview = new Overview
        {
            Pairs = [new Pair { Symbol = "A/B", Impact = [new Impact { NotionalUsd = 1 }], Reasons = ["stale:x", "stale:y"] }],
            Leaders = [new Leader { Exchange = "x", Share = 1 }],
            Implied = new Implied
            {
                Pegs = [new Peg { Symbol = "A/B" }],
                Fx = [new ImpliedFx { Ccy = "EUR", Implied = 1.1 }],
                Triangles = [new Triangle { Cross = "A/B" }],
            },
            Paths = [new ConversionPath { Sell = "A", Buy = "B", Tokens = ["A", "C", "B"], Ts = 1 }],
        };
        foreach (var section in Tables.OverviewSections)
        {
            var table = Tables.Overview(overview, section, true);
            Assert.NotNull(table);
            Assert.Equal(2, table!.GetLength(0));
            foreach (var value in table)
            {
                Assert.True(value is double or string, $"{section}: {value?.GetType()}");
            }
        }
        Assert.Equal("stale:x,stale:y", Tables.Overview(overview, "pairs", false)![0, 18]);
        Assert.Equal("A > C > B", Tables.Overview(overview, "paths", false)![0, 3]);
        Assert.Null(Tables.Overview(overview, "nope", true));
    }

    [Fact]
    public void ADocumentWithoutFieldsStillMakesEveryTable()
    {
        var empty = System.Text.Json.JsonSerializer.Deserialize("""{"pairs":[{"impact":[{}]}],"paths":[{}]}""", KlondikeJson.Default.Overview)!;
        foreach (var section in Tables.OverviewSections)
        {
            Assert.NotNull(Tables.Overview(empty, section, true));
        }
    }

    [Fact]
    public void SpreadInBpsIsAgainstTheMid()
    {
        var book = new Orderbook { Bids = [new Level { Price = 99 }], Asks = [new Level { Price = 101 }] };
        Assert.Equal(200d, (double)Fields.Book["spreadBps"](book)!, 6);
        // One side missing: no spread, not a spread against zero.
        var oneSide = new Orderbook { Bids = [new Level { Price = 99 }] };
        Assert.Null(Fields.Book["mid"](oneSide));
        Assert.Null(Fields.Book["spread"](oneSide));
        Assert.Null(Fields.Book["spreadBps"](oneSide));
    }
}
