using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ExcelDna.Integration;
using ExcelDna.Integration.CustomUI;

namespace Klondike.Excel;

/// <summary>The Klondike tab of the ribbon: the API key and the documentation.</summary>
[ComVisible(true)]
public sealed class Ribbon : ExcelRibbon
{
    /// <inheritdoc />
    public override string GetCustomUI(string ribbonId) => """
        <customUI xmlns="http://schemas.microsoft.com/office/2009/07/customui">
          <ribbon>
            <tabs>
              <tab id="klondikeTab" label="Klondike">
                <group id="klondikeGroup" label="Market data">
                  <button id="klondikeKey" label="API key" size="large" imageMso="AdpPrimaryKey"
                          screentip="Set the API key" onAction="OnApiKey" />
                  <button id="klondikeDocs" label="Documentation" size="large" imageMso="Help"
                          screentip="The functions of the add-in" onAction="OnDocumentation" />
                </group>
              </tab>
            </tabs>
          </ribbon>
        </customUI>
        """;

    /// <summary>The API key button.</summary>
    public void OnApiKey(IRibbonControl control)
    {
        if (ApiKeyStore.FromEnvironment)
        {
            MessageBox.Show(new ExcelWindow(), "The key comes from the environment variable KLONDIKE_API_KEY.", "Klondike API key", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new ApiKeyDialog(ApiKeyStore.Load() is not null);
        switch (dialog.ShowDialog(new ExcelWindow()))
        {
            case DialogResult.OK when dialog.Key.Length > 0:
                ApiKeyStore.Save(dialog.Key);
                AddIn.Hub.SetApiKey(dialog.Key);
                break;
            case DialogResult.Abort:
                ApiKeyStore.Save(null);
                AddIn.Hub.SetApiKey(null);
                break;
        }
    }

    /// <summary>The documentation button.</summary>
    public void OnDocumentation(IRibbonControl control) =>
        Process.Start(new ProcessStartInfo("https://klondike-trading.github.io/klondike-api/clients/excel/") { UseShellExecute = true });

    private sealed class ExcelWindow : IWin32Window
    {
        public IntPtr Handle => ExcelDnaUtil.WindowHandle;
    }
}
