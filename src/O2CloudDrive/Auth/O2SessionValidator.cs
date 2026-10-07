using System.Globalization;
using System.Diagnostics;
using System.Text.Json;

namespace O2CloudDrive.Auth;

/// <summary>
/// Confirms a session only when a read-only O2 storage probe returns a positive,
/// recognizable storage-quota payload. HTTP success or MIME alone is not authentication.
/// </summary>
public sealed class O2SessionValidator
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly string[] UsedNames = ["used", "usedspace", "storageused"];
    private static readonly string[] FreeNames = ["free", "freespace", "storagefree"];
    private static readonly string[] TotalNames = ["total", "totalspace", "quota", "capacity", "storagetotal"];

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly AuthSessionLog? _authSessionLog;
    private readonly TimeSpan _requestTimeout;

    public O2SessionValidator(HttpClient httpClient, string apiBaseUrl, AuthSessionLog? authSessionLog = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient;
        _baseUri = new Uri(apiBaseUrl, UriKind.Absolute);
        _authSessionLog = authSessionLog;
        _requestTimeout = requestTimeout ?? RequestTimeout;
    }

    public bool Validate(O2Session session)
        => ValidateDetailed(session).IsValid;

    /// <summary>
    /// Performs the same single read-only storage probe as Validate, but returns
    /// structural diagnostics that never contain request or response values.
    /// </summary>
    public O2SessionValidationDiagnostic ValidateDetailed(
        O2Session session,
        CancellationToken cancellationToken = default,
        bool storedSession = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var started = Stopwatch.GetTimestamp();
        var setCookies = SetCookieMetadataSummary.Empty;
        const string operation = "authenticated-storage-quota-probe";
        const string method = "GET";
        var endpoint = SafeEndpoint(new Uri(_baseUri, "media"));
        var validationKeyPresent = !string.IsNullOrWhiteSpace(session.ValidationKey);
        var cookieCount = session.Cookies.Count(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));
        var cookieHeaderPresent = !string.IsNullOrWhiteSpace(session.CookieHeader);
        var userAgentPresent = !string.IsNullOrWhiteSpace(session.UserAgent);

        O2SessionValidationDiagnostic Complete(O2SessionValidationDiagnostic diagnostic)
        {
            var completed = diagnostic with
            {
                DurationMilliseconds = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
                SetCookies = setCookies,
            };
            _authSessionLog?.LogValidation(completed, storedSession, session);
            return completed;
        }

        if (!session.IsAuthenticated)
        {
            return Complete(new O2SessionValidationDiagnostic(false, "validation_key_missing", operation, method, endpoint,
                null, "none", null, "not_requested", false, validationKeyPresent,
                cookieHeaderPresent, cookieCount, userAgentPresent, false, null, null, null, null));
        }

        if (string.IsNullOrWhiteSpace(session.DeviceId))
        {
            return Complete(new O2SessionValidationDiagnostic(false, "device_id_missing", operation, method, endpoint,
                null, "none", null, "not_requested", false, validationKeyPresent,
                cookieHeaderPresent, cookieCount, userAgentPresent, false, null, null, null, null));
        }

        HttpRequestMessage request;
        bool deviceIdHeaderMatchesSession;
        try
        {
            request = new HttpRequestMessage(HttpMethod.Get, BuildStorageProbeUri(session.ValidationKey));
            ApplySessionHeaders(request, session);
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-www-form-urlencoded");
            request.Options.Set(AuthSessionDiagnosticsHandler.RequestContextKey, new AuthenticatedRequestContext(
                operation, true, storedSession, validationKeyPresent, !string.IsNullOrWhiteSpace(session.DeviceId),
                cookieCount, userAgentPresent, IsSessionValidation: true));
            deviceIdHeaderMatchesSession = request.Headers.TryGetValues(O2SessionRequestIdentity.HeaderName, out var deviceValues) &&
                deviceValues.Count() == 1 && string.Equals(deviceValues.Single(), session.DeviceId, StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            return Complete(DiagnosticFailure("request_build_exception", endpoint, null, "none", null, "not_sent",
                false, validationKeyPresent, cookieHeaderPresent, cookieCount, userAgentPresent,
                false, exception, null, null));
        }

        using (request)
        {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_requestTimeout);
        HttpResponseMessage response;
        try
        {
            response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
        }
        catch (Exception exception)
        {
            var failureCondition = exception switch
            {
                OperationCanceledException when !cancellationToken.IsCancellationRequested => "timeout",
                OperationCanceledException => "cancelled",
                HttpRequestException => "network_failure",
                _ => "other_failure",
            };
            return Complete(DiagnosticFailure(failureCondition, endpoint, null, "none", null, "not_received",
                true, validationKeyPresent, cookieHeaderPresent, cookieCount, userAgentPresent,
                deviceIdHeaderMatchesSession, exception, null, null));
        }

        using (response)
        {
            if (!request.Options.TryGetValue(AuthSessionDiagnosticsHandler.ResponseSetCookiesKey, out setCookies))
            {
                setCookies = AuthSessionLog.CaptureSetCookies(response.Headers);
            }
            var status = (int)response.StatusCode;
            var mimeType = response.Content.Headers.ContentType?.MediaType ?? "none";
            var responseEndpoint = response.RequestMessage?.RequestUri is { } responseUri
                ? SafeEndpoint(responseUri)
                : endpoint;
            byte[] body;
            try
            {
                body = response.Content.ReadAsByteArrayAsync(requestTimeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                var failureCondition = exception is OperationCanceledException && !cancellationToken.IsCancellationRequested
                    ? "timeout"
                    : exception is OperationCanceledException
                        ? "cancelled"
                        : "response_body_read_failure";
                return Complete(DiagnosticFailure(failureCondition, responseEndpoint, status, mimeType, null,
                    "unreadable", true, validationKeyPresent, cookieHeaderPresent, cookieCount, userAgentPresent,
                    deviceIdHeaderMatchesSession, exception, status, null));
            }

            var text = DecodeResponseBody(body, response.Content.Headers.ContentType?.CharSet);
            var shape = EvaluateStorageShape(text);
            var condition = !response.IsSuccessStatusCode
                ? "http_status_not_success"
                : !IsJsonMimeType(mimeType)
                    ? "response_mime_not_json"
                    : shape.Condition;

            return Complete(new O2SessionValidationDiagnostic(shape.IsPositive && response.IsSuccessStatusCode && IsJsonMimeType(mimeType),
                condition, operation, method, responseEndpoint, status, mimeType, body.LongLength, shape.Summary,
                true, validationKeyPresent, cookieHeaderPresent, cookieCount, userAgentPresent,
                deviceIdHeaderMatchesSession, null, null, null, null));
        }
        }
    }

    internal static bool IsJsonResponse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        return IsJsonMimeType(response.Content.Headers.ContentType?.MediaType);
    }

    internal static bool HasPositiveStorageShape(string text)
        => EvaluateStorageShape(text).IsPositive;

    private static StorageEvaluation EvaluateStorageShape(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new StorageEvaluation(false, "response_body_empty", "empty_body");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new StorageEvaluation(false, "json_root_not_object", "json_" + root.ValueKind.ToString().ToLowerInvariant());
            }

            if (ContainsExplicitFailure(root))
            {
                return new StorageEvaluation(false, "explicit_api_failure", "json_object explicit_failure=true");
            }

            var hasUsed = ContainsNumericField(root, UsedNames);
            var hasFree = ContainsNumericField(root, FreeNames);
            var hasTotal = ContainsNumericField(root, TotalNames);
            if (!hasUsed)
            {
                return new StorageEvaluation(false, "storage_used_field_missing_or_non_numeric",
                    "json_object used_numeric=false free_numeric=" + hasFree + " total_numeric=" + hasTotal);
            }

            if (!hasFree && !hasTotal)
            {
                return new StorageEvaluation(false, "storage_free_or_total_field_missing_or_non_numeric",
                    "json_object used_numeric=true free_numeric=false total_numeric=false");
            }

            return new StorageEvaluation(true, "positive_storage_shape", "json_object used_numeric=true free_or_total_numeric=true explicit_failure=false");
        }
        catch (JsonException)
        {
            return new StorageEvaluation(false, "response_body_invalid_json",
                text.TrimStart().StartsWith('<') ? "html_or_markup_non_json" : "non_json_text");
        }
    }

    private static bool IsJsonMimeType(string? mediaType) => mediaType is not null &&
        (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Equals("text/javascript", StringComparison.OrdinalIgnoreCase));

    private static string DecodeResponseBody(byte[] body, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return System.Text.Encoding.GetEncoding(charset.Trim('"')).GetString(body); }
            catch (ArgumentException) { }
        }

        return System.Text.Encoding.UTF8.GetString(body);
    }

    private static string SafeEndpoint(Uri? uri) => uri is null
        ? "none"
        : uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath;

    private static O2SessionValidationDiagnostic DiagnosticFailure(string condition, string endpoint, int? status,
        string mimeType, long? responseLength, string bodySummary, bool httpSendAttempted,
        bool validationKeyPresent, bool cookieHeaderPresent, int cookieCount, bool userAgentPresent,
        bool deviceIdHeaderMatchesSession, Exception? exception, int? exceptionStatus, string? requestError)
    {
        var httpException = exception as HttpRequestException;
        return new O2SessionValidationDiagnostic(false, condition, "authenticated-storage-quota-probe", "GET", endpoint,
            status, mimeType, responseLength, bodySummary, httpSendAttempted, validationKeyPresent,
            cookieHeaderPresent, cookieCount, userAgentPresent, deviceIdHeaderMatchesSession,
            exception?.GetType().Name, exception?.InnerException?.GetType().Name,
            requestError ?? httpException?.HttpRequestError.ToString(), exceptionStatus ?? (int?)httpException?.StatusCode);
    }

    private readonly record struct StorageEvaluation(bool IsPositive, string Condition, string Summary);

    private static bool ContainsExplicitFailure(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined))
                {
                    return true;
                }

                if (property.Name.Equals("success", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.False)
                {
                    return true;
                }

                if (property.Name.Equals("status", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    (property.Value.GetString()?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ||
                     property.Value.GetString()?.Contains("fail", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return true;
                }

                if (ContainsExplicitFailure(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsExplicitFailure);
        }

        return false;
    }

    private Uri BuildStorageProbeUri(string validationKey)
    {
        var builder = new UriBuilder(new Uri(_baseUri, "media"))
        {
            Query = "action=get-storage-space&softdeleted=true&validationkey=" + Uri.EscapeDataString(validationKey),
        };
        return builder.Uri;
    }

    private void ApplySessionHeaders(HttpRequestMessage request, O2Session session)
    {
        if (!string.IsNullOrWhiteSpace(session.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
        }

        if (!string.IsNullOrWhiteSpace(session.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
        }

        request.Headers.TryAddWithoutValidation("Referer", _baseUri.GetLeftPart(UriPartial.Authority) + "/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "es-ES,es;q=0.9,en;q=0.8");
        O2SessionRequestIdentity.ApplyDeviceIdHeader(request, session);
    }

    private static bool ContainsNumericField(JsonElement element, IReadOnlyCollection<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && IsNumeric(property.Value))
                    {
                        return true;
                    }

                    if (ContainsNumericField(property.Value, names))
                    {
                        return true;
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsNumericField(item, names))
                    {
                        return true;
                    }
                }
                break;
        }

        return false;
    }

    private static bool IsNumeric(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out _) || value.TryGetDouble(out _);
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }
}

public sealed record O2SessionValidationDiagnostic(
    bool IsValid,
    string Condition,
    string Operation,
    string Method,
    string Endpoint,
    int? HttpStatus,
    string MimeType,
    long? ResponseLengthBytes,
    string ResponseSummary,
    bool HttpSendAttempted,
    bool ValidationKeyPresent,
    bool CookieHeaderPresent,
    int NonEmptyCookieCount,
    bool UserAgentPresent,
    bool DeviceIdHeaderMatchesSession,
    string? PrimaryExceptionType,
    string? InnerExceptionType,
    string? HttpRequestError,
    int? ExceptionStatusCode)
{
    public double DurationMilliseconds { get; init; }
    public SetCookieMetadataSummary SetCookies { get; init; } = SetCookieMetadataSummary.Empty;

    public SessionValidationClassification Classification
    {
        get
        {
            if (IsValid) return SessionValidationClassification.Accepted;
            if (Condition == "validation_key_missing") return SessionValidationClassification.MissingValidationKey;
            if (Condition == "device_id_missing") return SessionValidationClassification.MissingDeviceId;
            if (HttpStatus == (int)System.Net.HttpStatusCode.Unauthorized) return SessionValidationClassification.Unauthorized401;
            if (HttpStatus == (int)System.Net.HttpStatusCode.Forbidden) return SessionValidationClassification.Forbidden403;
            if (Condition == "timeout") return SessionValidationClassification.Timeout;
            if (Condition == "network_failure" ||
                HttpRequestError is not null ||
                PrimaryExceptionType == nameof(HttpRequestException) ||
                HttpStatus is 408 or 429 || HttpStatus is >= 500)
                return SessionValidationClassification.TransientNetworkFailure;
            if (HttpStatus is int status && (status < 200 || status >= 300))
                return SessionValidationClassification.UnexpectedHttpStatus;
            if (HttpStatus is >= 200 and < 300 || Condition.StartsWith("response_", StringComparison.Ordinal) ||
                Condition.StartsWith("storage_", StringComparison.Ordinal) || Condition.StartsWith("json_", StringComparison.Ordinal) ||
                Condition == "explicit_api_failure")
                return SessionValidationClassification.InvalidResponse;
            return SessionValidationClassification.OtherFailure;
        }
    }

    public string ToSafeSummary() =>
        "valid=" + IsValid +
        " classification=" + Classification +
        " condition=" + Condition +
        " operation=" + Operation +
        " method=" + Method +
        " endpoint=" + Endpoint +
        " status=" + (HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none") +
        " mime=" + MimeType +
        " response_bytes=" + (ResponseLengthBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown") +
        " duration_ms=" + DurationMilliseconds.ToString(CultureInfo.InvariantCulture) +
        " set_cookie_present=" + SetCookies.Present +
        " set_cookie_count=" + SetCookies.Count +
        " body_summary=" + ResponseSummary +
        " http_send_attempted=" + HttpSendAttempted +
        " validationkey_present=" + ValidationKeyPresent +
        " cookie_header_present=" + CookieHeaderPresent +
        " nonempty_cookie_count=" + NonEmptyCookieCount +
        " user_agent_present=" + UserAgentPresent +
        " device_id_header_matches_session=" + DeviceIdHeaderMatchesSession +
        " primary_exception=" + (PrimaryExceptionType ?? "none") +
        " inner_exception=" + (InnerExceptionType ?? "none") +
        " http_request_error=" + (HttpRequestError ?? "none") +
        " exception_status=" + (ExceptionStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none") +
        " secret_values_omitted=true";
}

public enum SessionValidationClassification
{
    Accepted,
    Unauthorized401,
    Forbidden403,
    MissingValidationKey,
    MissingDeviceId,
    TransientNetworkFailure,
    Timeout,
    UnexpectedHttpStatus,
    InvalidResponse,
    OtherFailure,
    MissingSession,
}
