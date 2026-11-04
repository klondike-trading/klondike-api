using ExcelDna.Integration;

namespace Klondike.Excel;

/// <summary>Excel loads and unloads the add-in here.</summary>
public sealed class AddIn : IExcelAddIn
{
    private static MarketHub? _hub;

    internal static MarketHub Hub => _hub ?? throw new InvalidOperationException("the add-in is not loaded");

    /// <inheritdoc />
    public void AutoOpen()
    {
        ExcelIntegration.RegisterUnhandledExceptionHandler(e => Cells.Error(Convert.ToString(e) ?? "error"));
        _hub = new MarketHub(new MarketHubOptions
        {
            ApiKey = ApiKeyStore.Load(),
            BaseUrl = Environment.GetEnvironmentVariable("KLONDIKE_API_URL") ?? KlondikeClient.DefaultBaseUrl,
        });
    }

    /// <inheritdoc />
    public void AutoClose()
    {
        // Excel is waiting: the connections are closed anyway when it exits.
        _hub?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _hub = null;
    }
}
