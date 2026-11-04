// Prints the trades of a symbol as they happen:
//   KLONDIKE_API_KEY=kl_... dotnet run --project examples/StreamTrades -- BTC/USDT
using Klondike;

var symbol = args.FirstOrDefault() ?? "BTC/USDT";
using var client = new KlondikeClient(Environment.GetEnvironmentVariable("KLONDIKE_API_KEY") ?? "", new KlondikeClientOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("KLONDIKE_API_URL") ?? KlondikeClient.DefaultBaseUrl,
});
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

await using var stream = await client.OpenStreamAsync(new StreamOptions { Format = StreamFormat.Capnp });
await stream.SubscribeTradesAsync(symbol);
try
{
    await foreach (var message in stream.ReadAllAsync(stop.Token))
    {
        switch (message)
        {
            case TradesMessage { Trades: var batch }:
                foreach (var trade in batch.Trades)
                {
                    Console.WriteLine($"{trade.Time:HH:mm:ss.fff} {trade.Venue,-14} {trade.Side,-7} {trade.Price,12} {trade.Qty}");
                }
                break;
            case ErrorMessage { Error: var error }:
                Console.Error.WriteLine(error.Message);
                break;
        }
    }
    Console.Error.WriteLine(stream.Error);
}
catch (OperationCanceledException)
{
    // Ctrl-C.
}
