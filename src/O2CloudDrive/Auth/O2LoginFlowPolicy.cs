using System.Text;

namespace O2CloudDrive.Auth;

/// <summary>
/// Narrow policy for advancing from O2 Cloud's public web landing page to its
/// own phone/SMS login. It deliberately does not contain or construct an SSO URL.
/// </summary>
public static class O2LoginFlowPolicy
{
    private const string CloudHost = "cloud.o2online.es";

    public static bool ShouldAdvanceToWebLogin(
        Uri currentUri,
        IEnumerable<string> visibleHeadings,
        IEnumerable<string> visibleButtonLabels,
        bool cookieConsentVisible)
    {
        if (cookieConsentVisible || !IsCloudLoginLanding(currentUri))
        {
            return false;
        }

        var hasLandingHeading = visibleHeadings.Any(value =>
            Normalize(value).Contains("tus contenidos siempre a salvo", StringComparison.Ordinal));
        var accessButtons = visibleButtonLabels.Count(value => Normalize(value) == "acceder");

        return hasLandingHeading && accessButtons == 1;
    }

    public static bool IsCloudLoginLanding(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, CloudHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath.TrimEnd('/'), "/login", StringComparison.OrdinalIgnoreCase);

    public static bool IsO2OwnedHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        (string.Equals(uri.IdnHost, "o2online.es", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".o2online.es", StringComparison.OrdinalIgnoreCase));

    public static bool IsAppStoreUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        (string.Equals(uri.IdnHost, "play.google.com", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.IdnHost, "apps.apple.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Logs a route without query/fragment values, which may carry OIDC state.</summary>
    public static string SanitizeUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "[invalid-url]";
        }

        var safe = $"{uri.Scheme}://{uri.IdnHost}{uri.AbsolutePath}";
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            var fragmentRoute = uri.Fragment[1..].Split('?', 2)[0];
            safe += fragmentRoute.Length is > 0 and <= 100 && fragmentRoute.StartsWith('/')
                ? "#" + fragmentRoute
                : "#[redacted]";
            if (uri.Fragment.Contains('?', StringComparison.Ordinal))
            {
                safe += "?[redacted]";
            }
        }
        else if (!string.IsNullOrEmpty(uri.Query))
        {
            safe += "?[redacted]";
        }

        return safe;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) !=
                System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return string.Join(' ', builder.ToString().Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
