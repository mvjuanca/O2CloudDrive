using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using O2CloudDrive.Auth;

internal sealed class SafeHttpTraceHandler(
    Action<string> log,
    Func<string> currentPhase,
    Func<O2Session?> currentSession,
    CookieContainer cookieContainer) : DelegatingHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var phase = LogRequest(request);
        try
        {
            var response = base.Send(request, cancellationToken);
            LogResponse(request, response, phase);
            return response;
        }
        catch (Exception ex)
        {
            LogFailure(request, ex, phase);
            throw;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var phase = LogRequest(request);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            LogResponse(request, response, phase);
            return response;
        }
        catch (Exception ex)
        {
            LogFailure(request, ex, phase);
            throw;
        }
    }

    private string LogRequest(HttpRequestMessage request)
    {
        var requestUri = request.RequestUri;
        var session = currentSession();
        var hasManualCookie = request.Headers.Contains("Cookie");
        var explicitCookieCount = session?.Cookies.Count(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)) ?? 0;
        var jarCookieCount = requestUri is null ? 0 : cookieContainer.GetCookies(requestUri).Count;
        var ua = request.Headers.UserAgent.ToString();
        var uaMatchesSession = session is not null &&
                               !string.IsNullOrWhiteSpace(session.UserAgent) &&
                               string.Equals(ua, session.UserAgent, StringComparison.Ordinal);
        var queryKeys = requestUri is null ? "none" : QueryKeys(requestUri);
        var contentType = request.Content?.Headers.ContentType?.MediaType ?? "none";
        var phase = SafeText(currentPhase());

        log($"HTTP_REQUEST phase={phase} method={request.Method.Method} route={SafeRoute(requestUri)} queryKeys={queryKeys} " +
            $"validationkey_present={HasQueryKey(requestUri, "validationkey")} cookie_header_present={hasManualCookie} " +
            $"session_cookie_count={explicitCookieCount} cookie_jar_count_for_host={jarCookieCount} " +
            $"user_agent_present={!string.IsNullOrWhiteSpace(ua)} user_agent_matches_session={uaMatchesSession} " +
            $"origin_present={request.Headers.Contains("Origin")} referer_present={request.Headers.Contains("Referer")} " +
            $"x_deviceid_present={request.Headers.Contains("X-deviceid")} x_requested_with_present={request.Headers.Contains("X-Requested-With")} " +
            $"accept={AcceptTypes(request.Headers.Accept)} request_content_type={contentType} " +
            $"request_version={request.Version} body_present={request.Content is not null}");
        return phase;
    }

    private void LogResponse(HttpRequestMessage request, HttpResponseMessage response, string phase)
    {
        var requestUri = request.RequestUri;
        var responseUri = response.RequestMessage?.RequestUri;
        var location = response.Headers.Location;
        var absoluteLocation = location is null || requestUri is null
            ? null
            : location.IsAbsoluteUri ? location : new Uri(requestUri, location);
        var responseType = response.Content.Headers.ContentType?.MediaType ?? "none";
        var length = response.Content.Headers.ContentLength?.ToString() ?? "unknown";
        var reason = SafeText(response.ReasonPhrase ?? "");
        log($"HTTP_RESPONSE phase={phase} status={(int)response.StatusCode} reason={reason} response_version={response.Version} " +
            $"content_type={responseType} content_length={length} final_route={SafeRoute(responseUri)} " +
            $"redirect_location={SafeRoute(absoluteLocation)}");
    }

    private void LogFailure(HttpRequestMessage request, Exception exception, string phase)
    {
        log($"HTTP_EXCEPTION phase={phase} method={request.Method.Method} route={SafeRoute(request.RequestUri)} " +
            $"exception_chain={FormatExceptionChain(exception)}");
    }

    internal static string FormatExceptionChain(Exception exception)
    {
        var items = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var detail = $"{current.GetType().FullName}: {SafeText(current.Message)}";
            if (current is HttpRequestException http)
            {
                detail += $"; http_request_error={http.HttpRequestError}; status_code={http.StatusCode?.ToString() ?? "none"}";
            }

            items.Add(detail);
        }

        return string.Join(" -> ", items);
    }

    private static string SafeRoute(Uri? uri) => uri is null ? "none" : O2LoginFlowPolicy.SanitizeUri(uri.ToString());

    private static string QueryKeys(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(uri.Query)) return "none";
        return string.Join(',', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Select(key => Uri.UnescapeDataString(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
    }

    private static bool HasQueryKey(Uri? uri, string name) =>
        uri is not null && uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Uri.UnescapeDataString(part.Split('=', 2)[0]))
            .Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string AcceptTypes(IEnumerable<MediaTypeWithQualityHeaderValue> values) =>
        !values.Any() ? "none" : string.Join(',', values.Select(value => value.MediaType));

    private static string SafeText(string value)
    {
        var sanitized = Regex.Replace(value, @"https?://[^\s\""'<>]+", match => SafeUrlInText(match.Value));
        sanitized = Regex.Replace(sanitized,
            @"(?i)\b(validationkey|cookie|authorization|access_token|refresh_token|token|phone|msisdn|mobile|pin|otp|password|state|sessiondata|nonce)\s*[:=]\s*[^&\s;,'\""<>]+",
            "$1=[redacted]");
        return sanitized.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string SafeUrlInText(string candidate)
    {
        var trimmed = candidate.TrimEnd('.', ')', ']', ',');
        return O2LoginFlowPolicy.SanitizeUri(trimmed);
    }
}
