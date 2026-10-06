using System.Net;
using System.Text;
using System.Text.Json;

namespace O2CloudDrive.Api;

/// <summary>Sanitized API failure details; raw response bodies and query values are never retained.</summary>
internal sealed class O2CloudApiException : IOException
{
    private const int MaxDiagnosticBodyBytes = 4096;

    private O2CloudApiException(
        string operation,
        string endpoint,
        HttpStatusCode? statusCode,
        string responseSummary,
        Exception? innerException)
        : base(BuildMessage(operation, endpoint, statusCode, responseSummary), innerException)
    {
        Operation = operation;
        Endpoint = endpoint;
        StatusCode = statusCode;
        SanitizedResponseSummary = responseSummary;
    }

    internal string Operation { get; }
    internal string Endpoint { get; }
    internal HttpStatusCode? StatusCode { get; }
    internal string SanitizedResponseSummary { get; }

    internal static O2CloudApiException FromResponse(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string operation,
        Exception? innerException = null)
    {
        var body = ReadBodySample(response.Content);
        var mime = response.Content.Headers.ContentType?.MediaType;
        return new O2CloudApiException(
            operation,
            SafeEndpoint(request.RequestUri),
            response.StatusCode,
            SummarizeBody(body, mime),
            innerException);
    }

    internal static O2CloudApiException FromApplicationError(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string operation,
        string body,
        Exception? innerException = null)
    {
        return new O2CloudApiException(
            operation,
            SafeEndpoint(request.RequestUri),
            response.StatusCode,
            SummarizeBody(body, response.Content.Headers.ContentType?.MediaType),
            innerException);
    }

    internal static string SummarizeBody(string? body, string? mime)
    {
        var sample = body ?? string.Empty;
        var lower = sample.ToLowerInvariant();
        var isHtml = lower.Contains("<html", StringComparison.Ordinal) || lower.Contains("<!doctype html", StringComparison.Ordinal);
        var isJson = false;
        var hasErrorField = false;
        var hasFailureValue = false;
        try
        {
            using var document = JsonDocument.Parse(sample);
            isJson = true;
            hasErrorField = ContainsProperty(document.RootElement, "error");
            hasFailureValue = ContainsFailure(document.RootElement);
        }
        catch (JsonException)
        {
            // Only structural markers are retained; the response text is deliberately discarded.
        }

        var loginMarker = lower.Contains("login", StringComparison.Ordinal) ||
                          lower.Contains("sign in", StringComparison.Ordinal) ||
                          lower.Contains("password", StringComparison.Ordinal);
        var authMarker = lower.Contains("unauthorized", StringComparison.Ordinal) ||
                         lower.Contains("authentication", StringComparison.Ordinal) ||
                         lower.Contains("session", StringComparison.Ordinal);
        return $"mime={SafeMime(mime)}; sample_bytes<={Encoding.UTF8.GetByteCount(sample)}; json={isJson}; html={isHtml}; " +
               $"error_field={hasErrorField}; failure_value={hasFailureValue}; login_marker={loginMarker}; auth_marker={authMarker}; body_values_omitted=true";
    }

    private static string ReadBodySample(HttpContent? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        using var stream = content.ReadAsStream();
        var buffer = new byte[MaxDiagnosticBodyBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string SafeEndpoint(Uri? uri)
    {
        if (uri is null) return "unknown-endpoint";
        if (uri.AbsolutePath.StartsWith("/sapi/", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath;
        }

        return uri.IdnHost.Equals("cloud.o2online.es", StringComparison.OrdinalIgnoreCase)
            ? "o2-cloud-download-endpoint"
            : "external-download-endpoint";
    }

    private static string SafeMime(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime)) return "unknown";
        return mime.Length <= 80 && mime.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '/' or '+' or '-' or '.')
            ? mime
            : "other";
    }

    private static bool ContainsProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) ||
                    ContainsProperty(property.Value, propertyName)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(item => ContainsProperty(item, propertyName));
        }

        return false;
    }

    private static bool ContainsFailure(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("success", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.False) return true;
                if (property.Name.Equals("status", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } status &&
                    (status.Contains("error", StringComparison.OrdinalIgnoreCase) || status.Contains("fail", StringComparison.OrdinalIgnoreCase))) return true;
                if (ContainsFailure(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsFailure);
        }

        return false;
    }

    private static string BuildMessage(string operation, string endpoint, HttpStatusCode? statusCode, string summary)
    {
        var status = statusCode is null ? "" : $" HTTP {(int)statusCode.Value}";
        return $"O2 Cloud {operation} failed at {endpoint}{status}; {summary}";
    }
}
