using System.Security.Cryptography;
using System.Text;

namespace Klondike.Excel;

/// <summary>
/// The API key of the user: KLONDIKE_API_KEY, or the file written from the ribbon, encrypted for this
/// Windows account (DPAPI). Never in a formula: it would travel with the workbook.
/// </summary>
internal static class ApiKeyStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Klondike", "apikey.bin");

    public static bool FromEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KLONDIKE_API_KEY"));

    public static string? Load()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("KLONDIKE_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }
        try
        {
            return File.Exists(FilePath)
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null; // written by another account or machine: the status of the add-in asks for a key
        }
    }

    public static void Save(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), null, DataProtectionScope.CurrentUser));
    }
}
