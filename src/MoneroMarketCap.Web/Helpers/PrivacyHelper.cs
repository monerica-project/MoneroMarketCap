namespace MoneroMarketCap.Web.Helpers;

public static class PrivacyHelper
{
    public const string Mask = "••••";

    public static string Redact(bool privacyMode, string value) =>
        privacyMode ? Mask : value;
}