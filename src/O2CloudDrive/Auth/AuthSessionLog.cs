using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace O2CloudDrive.Auth;

public sealed class AuthSessionLog
{
    private static readonly Regex CookieNamePattern = new("^[A-Za-z0-9._-]{1,80}$", RegexOptions.CultureInvariant);
    private static readonly Regex DomainPattern = new("^\\.?[A-Za-z0-9.-]{1,253}$", RegexOptions.CultureInvariant);
    private static readonly Regex PathPattern = new("^/[A-Za-z0-9._~/-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _path;

    public AuthSessionLog(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "O2CloudDrive", "Logs", "auth-session.log");
    }

    public string LogPath => _path;

    public static string CreateLogicalOperation(HttpMethod method, Uri? uri) =>
        SafeLabel(method.Method) + " " + SafeEndpoint(uri?.AbsoluteUri);

    public void LogValidation(O2SessionValidationDiagnostic diagnostic, bool storedSession, O2Session session)
    {
        Write(new
        {
            timestamp = DateTimeOffset.Now,
            eventType = "session_validation",
            operation = SafeLabel(diagnostic.Operation),
            classification = diagnostic.Classification.ToString(),
            status = diagnostic.HttpStatus,
            endpoint = SafeEndpoint(diagnostic.Endpoint),
            mimeType = SafeMime(diagnostic.MimeType),
            responseLengthBytes = diagnostic.ResponseLengthBytes,
            durationMs = diagnostic.DurationMilliseconds,
            exceptionType = SafeLabel(diagnostic.PrimaryExceptionType),
            innerExceptionType = SafeLabel(diagnostic.InnerExceptionType),
            httpRequestError = SafeLabel(diagnostic.HttpRequestError),
            exceptionStatus = diagnostic.ExceptionStatusCode,
            storedSessionExists = storedSession,
            validationKeyPresent = !string.IsNullOrWhiteSpace(session.ValidationKey),
            deviceIdPresent = !string.IsNullOrWhiteSpace(session.DeviceId),
            cookieCount = session.Cookies.Count(pair =>
                !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)),
            userAgentPresent = !string.IsNullOrWhiteSpace(session.UserAgent),
            setCookiePresent = diagnostic.SetCookies.Present,
            setCookieCount = diagnostic.SetCookies.Count,
            setCookies = diagnostic.SetCookies.Cookies,
        });
    }

    public void LogAuthenticatedResponse(
        AuthenticatedRequestContext context,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TimeSpan duration,
        SetCookieMetadataSummary setCookies)
    {
        Write(new
        {
            timestamp = DateTimeOffset.Now,
            eventType = "authenticated_api_response",
            operation = SafeLabel(context.Operation),
            method = SafeLabel(request.Method.Method),
            endpoint = SafeEndpoint(request.RequestUri?.GetLeftPart(UriPartial.Authority) + request.RequestUri?.AbsolutePath),
            status = (int)response.StatusCode,
            mimeType = SafeMime(response.Content.Headers.ContentType?.MediaType),
            responseLengthBytesApprox = response.Content.Headers.ContentLength,
            durationMs = Math.Round(duration.TotalMilliseconds, 1),
            sessionPresent = context.SessionPresent,
            storedSessionExists = context.StoredSessionExists,
            validationKeyPresent = context.ValidationKeyPresent,
            deviceIdPresent = context.DeviceIdPresent,
            cookieCount = context.CookieCount,
            userAgentPresent = context.UserAgentPresent,
            setCookiePresent = setCookies.Present,
            setCookieCount = setCookies.Count,
            setCookies = setCookies.Cookies,
        });
    }

    public void LogAuthenticatedFailure(
        AuthenticatedRequestContext context,
        HttpRequestMessage request,
        TimeSpan duration,
        Exception exception)
    {
        Write(new
        {
            timestamp = DateTimeOffset.Now,
            eventType = "authenticated_api_failure",
            operation = SafeLabel(context.Operation),
            method = SafeLabel(request.Method.Method),
            endpoint = SafeEndpoint(request.RequestUri?.GetLeftPart(UriPartial.Authority) + request.RequestUri?.AbsolutePath),
            classification = exception is OperationCanceledException
                ? SessionValidationClassification.Timeout.ToString()
                : exception is HttpRequestException
                    ? SessionValidationClassification.TransientNetworkFailure.ToString()
                    : SessionValidationClassification.OtherFailure.ToString(),
            durationMs = Math.Round(duration.TotalMilliseconds, 1),
            exceptionType = SafeLabel(exception.GetType().Name),
            sessionPresent = context.SessionPresent,
            storedSessionExists = context.StoredSessionExists,
            validationKeyPresent = context.ValidationKeyPresent,
            deviceIdPresent = context.DeviceIdPresent,
            cookieCount = context.CookieCount,
            userAgentPresent = context.UserAgentPresent,
        });
    }

    public void LogWebViewProfileState(string profilePath, bool existedBeforeLogin)
    {
        var exists = Directory.Exists(profilePath);
        DateTimeOffset? modifiedApprox = null;
        if (exists)
        {
            try { modifiedApprox = new DateTimeOffset(Directory.GetLastWriteTimeUtc(profilePath), TimeSpan.Zero); }
            catch { }
        }

        Write(new
        {
            timestamp = DateTimeOffset.Now,
            eventType = "webview_profile",
            profileFoundBeforeLogin = existedBeforeLogin,
            folderExists = exists,
            folderKind = Path.GetFileName(profilePath).StartsWith("WebView2", StringComparison.OrdinalIgnoreCase)
                ? "O2CloudDrive-WebView2"
                : "other",
            lastModifiedApproxUtc = modifiedApprox,
        });
    }

    public void LogWebViewCookieMetadata(IReadOnlyCollection<WebViewCookieMetadata> cookies, int persistedCookieCount)
    {
        Write(new
        {
            timestamp = DateTimeOffset.Now,
            eventType = "webview_login_cookie_metadata",
            webViewCookieCount = cookies.Count,
            persistedCookieCount,
            cookies = cookies.Select(cookie => new
            {
                name = SafeCookieName(cookie.Name),
                domain = SafeDomain(cookie.Domain),
                path = SafeCookiePath(cookie.Path),
                httpOnly = cookie.IsHttpOnly,
                secure = cookie.IsSecure,
                session = cookie.IsSession,
                expiresUtc = cookie.ExpiresUtc,
            }).ToArray(),
        });
    }

    public static SetCookieMetadataSummary CaptureSetCookies(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Set-Cookie", out var values))
        {
            return SetCookieMetadataSummary.Empty;
        }

        var cookies = values.Select(ParseSetCookie).ToArray();
        return new SetCookieMetadataSummary(cookies.Length > 0, cookies.Length, cookies);
    }

    private static SetCookieMetadata ParseSetCookie(string header)
    {
        var parts = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var first = parts.FirstOrDefault() ?? string.Empty;
        var equals = first.IndexOf('=');
        var name = equals > 0 ? first[..equals].Trim() : first.Trim();
        string? domain = null;
        string? path = null;
        DateTimeOffset? expires = null;
        long? maxAge = null;
        var httpOnly = false;
        var secure = false;
        string? sameSite = null;

        foreach (var part in parts.Skip(1))
        {
            var index = part.IndexOf('=');
            var attribute = (index >= 0 ? part[..index] : part).Trim();
            var value = index >= 0 ? part[(index + 1)..].Trim().Trim('"') : string.Empty;
            if (attribute.Equals("domain", StringComparison.OrdinalIgnoreCase)) domain = SafeDomain(value);
            else if (attribute.Equals("path", StringComparison.OrdinalIgnoreCase)) path = SafeCookiePath(value);
            else if (attribute.Equals("expires", StringComparison.OrdinalIgnoreCase) &&
                     DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedExpires))
                expires = parsedExpires;
            else if (attribute.Equals("max-age", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxAge))
                maxAge = parsedMaxAge;
            else if (attribute.Equals("httponly", StringComparison.OrdinalIgnoreCase)) httpOnly = true;
            else if (attribute.Equals("secure", StringComparison.OrdinalIgnoreCase)) secure = true;
            else if (attribute.Equals("samesite", StringComparison.OrdinalIgnoreCase))
                sameSite = value.ToLowerInvariant() is "strict" or "lax" or "none" ? value.ToLowerInvariant() : "other";
        }

        return new SetCookieMetadata(SafeCookieName(name), domain, path, expires, maxAge,
            expires is null && maxAge is null, httpOnly, secure, sameSite);
    }

    private static string SafeCookieName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && CookieNamePattern.IsMatch(value) ? value : "omitted";

    private static string? SafeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return DomainPattern.IsMatch(normalized) ? normalized : null;
    }

    private static string? SafeCookiePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && PathPattern.IsMatch(value) ? value : null;

    private static string SafeEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "none";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var count = segments.Length > 0 && segments[0].Equals("sapi", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var path = segments.Length == 0 ? "/" : "/" + string.Join('/', segments.Take(count));
        var authority = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.IdnHost}"
            : $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";
        return authority + path;
    }

    private static string SafeMime(string? value) => SafeLabel(value, 96);

    private static string SafeLabel(string? value, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var safe = new string(value.Where(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ':' or '/').Take(maxLength).ToArray());
        return safe.Length == 0 ? "none" : safe;
    }

    private void Write<T>(T entry)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            lock (WriteGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostics must never change authentication or API behavior.
        }
    }
}

public sealed record AuthenticatedRequestContext(
    string Operation,
    bool SessionPresent,
    bool? StoredSessionExists,
    bool ValidationKeyPresent,
    bool DeviceIdPresent,
    int CookieCount,
    bool UserAgentPresent,
    bool IsSessionValidation = false);

public sealed record SetCookieMetadata(
    string Name,
    string? Domain,
    string? Path,
    DateTimeOffset? ExpiresUtc,
    long? MaxAgeSeconds,
    bool IsSession,
    bool IsHttpOnly,
    bool IsSecure,
    string? SameSite);

public sealed record SetCookieMetadataSummary(bool Present, int Count, IReadOnlyList<SetCookieMetadata> Cookies)
{
    public static SetCookieMetadataSummary Empty { get; } = new(false, 0, Array.Empty<SetCookieMetadata>());
}

public sealed record WebViewCookieMetadata(
    string Name,
    string Domain,
    string Path,
    bool IsHttpOnly,
    bool IsSecure,
    bool IsSession,
    DateTimeOffset? ExpiresUtc);
