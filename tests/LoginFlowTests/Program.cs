using O2CloudDrive.Auth;
using System.Net;
using System.Net.Http.Headers;

var passed = 0;
var failed = 0;

void Test(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine("PASS " + name);
    }
    else
    {
        failed++;
        Console.WriteLine("FAIL " + name);
    }
}

var landing = new Uri("https://cloud.o2online.es/login");
var hero = new[] { "Tus contenidos siempre a salvo. Disfruta 10TB de espacio en la nube" };
var access = new[] { "Aceptar todas las cookies", "Acceder" };

Test("official landing with unique web CTA advances", O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, hero, access, cookieConsentVisible: false));
Test("cookie consent blocks auto navigation", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, hero, access, cookieConsentVisible: true));
Test("QR/app promotion alone is not selected", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, new[] { "Tus contenidos siempre a salvo" }, new[] { "Obtener la App" }, false));
Test("phone form Acceder button is not auto-submitted", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(new Uri("https://t3.o2online.es/acceso/#/accessUserPassO2"), hero, new[] { "Acceder" }, false));
Test("landing marker is required", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, new[] { "Otra página" }, new[] { "Acceder" }, false));
Test("one exact Acceder button is required", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, hero, new[] { "Acceder", "Acceder" }, false));
Test("spoofed cloud hostname is rejected", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(new Uri("https://cloud.o2online.es.attacker.invalid/login"), hero, new[] { "Acceder" }, false));
Test("app store host is identified and blocked", O2LoginFlowPolicy.IsAppStoreUri(new Uri("https://play.google.com/store/apps/details?id=example")) && O2LoginFlowPolicy.IsAppStoreUri(new Uri("https://apps.apple.com/es/app/example")));
Test("O2 SSO popup stays in current WebView", O2LoginFlowPolicy.IsO2OwnedHttpsUri(new Uri("https://t3.o2online.es/acceso/")));
Test("external/store popup is not trusted", !O2LoginFlowPolicy.IsO2OwnedHttpsUri(new Uri("https://play.google.com/store/apps/details?id=example")));

var unsafeUrl = "https://t3.o2online.es/acceso/#/accessUserPassO2?state=state-secret&sessionData=session-secret";
var safeUrl = O2LoginFlowPolicy.SanitizeUri(unsafeUrl);
Test("diagnostic route redacts dynamic query and hash values", safeUrl.Contains("#/accessUserPassO2?[redacted]", StringComparison.Ordinal) && !safeUrl.Contains("state-secret", StringComparison.Ordinal) && !safeUrl.Contains("session-secret", StringComparison.Ordinal));

var identitySession = new O2Session { ValidationKey = "OFFLINE_VALIDATION", DeviceId = "OFFLINE_SESSION_DEVICE" };
using (var identityRequest = new HttpRequestMessage(HttpMethod.Get, "https://cloud.o2online.es/sapi/media"))
{
    identityRequest.Headers.TryAddWithoutValidation("X-Deviceid", "STALE_TEST_VALUE");
    O2SessionRequestIdentity.ApplyDeviceIdHeader(identityRequest, identitySession);
    var values = identityRequest.Headers.GetValues(O2SessionRequestIdentity.HeaderName).ToArray();
    Test("authenticated SAPI request uses only the session DeviceId",
        values.Length == 1 && values[0] == identitySession.DeviceId &&
        !identityRequest.Headers.Contains("Authorization"));
}

using (var missingIdentityRequest = new HttpRequestMessage(HttpMethod.Get, "https://cloud.o2online.es/sapi/media"))
{
    var rejected = false;
    try { O2SessionRequestIdentity.ApplyDeviceIdHeader(missingIdentityRequest, identitySession with { DeviceId = "" }); }
    catch (InvalidOperationException) { rejected = true; }
    Test("missing session DeviceId is rejected before a request is sent",
        rejected && !missingIdentityRequest.Headers.Contains(O2SessionRequestIdentity.HeaderName));
}

using (var unauthenticatedRequest = new HttpRequestMessage(HttpMethod.Get, "https://cloud.o2online.es/sapi/media"))
{
    var rejected = false;
    try { O2SessionRequestIdentity.ApplyDeviceIdHeader(unauthenticatedRequest, identitySession with { ValidationKey = "" }); }
    catch (InvalidOperationException) { rejected = true; }
    Test("unauthenticated session cannot apply a DeviceId header",
        rejected && !unauthenticatedRequest.Headers.Contains(O2SessionRequestIdentity.HeaderName));
}

bool ValidateResponse(HttpStatusCode statusCode, string mediaType, string body)
{
    using var handler = new ValidatorResponseHandler(statusCode, mediaType, body);
    using var client = new HttpClient(handler);
    var validator = new O2SessionValidator(client, "https://cloud.o2online.es/sapi/");
    return validator.Validate(new O2Session
    {
        ValidationKey = "TEST_KEY_NOT_REAL",
        UserAgent = "SafeTest/1.0",
        DeviceId = "TEST_DEVICE_NOT_REAL",
        Cookies = new Dictionary<string, string> { ["sid"] = "TEST_COOKIE_NOT_REAL" },
    });
}

Test("strict validator rejects 200 JavaScript", !ValidateResponse(HttpStatusCode.OK, "text/javascript", "window.status='ok';"));
Test("strict validator rejects 200 HTML", !ValidateResponse(HttpStatusCode.OK, "text/html", "<html><body>O2</body></html>"));
Test("strict validator rejects 401", !ValidateResponse(HttpStatusCode.Unauthorized, "application/json", "{\"data\":{\"used\":2,\"free\":8}}"));
Test("strict validator rejects JSON error without quota evidence", !ValidateResponse(HttpStatusCode.OK, "application/json", "{\"error\":{\"code\":401,\"message\":\"unauthorized\"}}"));
Test("strict validator rejects JSON error even when quota fields are present", !ValidateResponse(HttpStatusCode.OK, "application/json", "{\"error\":\"unauthorized\",\"data\":{\"used\":2,\"free\":8}}"));
Test("strict validator accepts JSON with used and free storage fields", ValidateResponse(HttpStatusCode.OK, "application/json", "{\"data\":{\"usedspace\":2,\"freespace\":8}}"));
Test("strict validator accepts text/javascript when it contains valid quota JSON", ValidateResponse(HttpStatusCode.OK, "text/javascript", "{\"data\":{\"usedspace\":2,\"freespace\":8}}"));
Test("strict validator rejects text/javascript with unrelated JSON", !ValidateResponse(HttpStatusCode.OK, "text/javascript", "{\"data\":{\"message\":\"ok\"}}"));

var diagnosticSession = new O2Session
{
    ValidationKey = "VALIDATION_SECRET",
    DeviceId = "DEVICE_SECRET",
    UserAgent = "SafeDiagnostic/1.0",
    Cookies = new Dictionary<string, string> { ["sid"] = "COOKIE_SECRET" },
};
var successDiagnostic = DiagnosticValidationFixtures.ValidateDetailedResponse(HttpStatusCode.OK, "text/javascript",
    "{\"data\":{\"usedspace\":12,\"freespace\":88}}", diagnosticSession);
var successSummary = successDiagnostic.Result.ToSafeSummary();
Test("diagnostic pass records method endpoint status MIME length and positive JSON shape",
    successDiagnostic.Result.IsValid && successDiagnostic.Result.Condition == "positive_storage_shape" &&
    successDiagnostic.Result.Method == "GET" && successDiagnostic.Result.Endpoint == "https://cloud.o2online.es/sapi/media" &&
    successDiagnostic.Result.HttpStatus == 200 && successDiagnostic.Result.MimeType == "text/javascript" &&
    successDiagnostic.Result.ResponseLengthBytes == System.Text.Encoding.UTF8.GetByteCount("{\"data\":{\"usedspace\":12,\"freespace\":88}}") &&
    successDiagnostic.Result.ResponseSummary.Contains("used_numeric=true", StringComparison.Ordinal) &&
    successDiagnostic.Result.HttpSendAttempted && successDiagnostic.Result.DeviceIdHeaderMatchesSession &&
    successDiagnostic.RequestCount == 1);
Test("safe validation diagnostics omit validation key cookie device ID and response values",
    !successSummary.Contains("VALIDATION_SECRET", StringComparison.Ordinal) &&
    !successSummary.Contains("COOKIE_SECRET", StringComparison.Ordinal) &&
    !successSummary.Contains("DEVICE_SECRET", StringComparison.Ordinal) &&
    !successSummary.Contains("usedspace\\\":12", StringComparison.Ordinal));

var deniedDiagnostic = DiagnosticValidationFixtures.ValidateDetailedResponse(HttpStatusCode.Unauthorized, "text/html",
    "<html>LOGIN_BODY_SECRET</html>", diagnosticSession);
Test("diagnostic identifies 401 before MIME rejection and summarizes markup without content",
    !deniedDiagnostic.Result.IsValid && deniedDiagnostic.Result.Condition == "http_status_not_success" &&
    deniedDiagnostic.Result.HttpStatus == 401 && deniedDiagnostic.Result.MimeType == "text/html" &&
    deniedDiagnostic.Result.ResponseSummary == "html_or_markup_non_json" &&
    !deniedDiagnostic.Result.ToSafeSummary().Contains("LOGIN_BODY_SECRET", StringComparison.Ordinal));

var unrelatedDiagnostic = DiagnosticValidationFixtures.ValidateDetailedResponse(HttpStatusCode.OK, "text/javascript",
    "{\"data\":{\"message\":\"BODY_VALUE_SECRET\"}}", diagnosticSession);
Test("diagnostic pinpoints missing positive storage fields without exposing JSON values",
    !unrelatedDiagnostic.Result.IsValid &&
    unrelatedDiagnostic.Result.Condition == "storage_used_field_missing_or_non_numeric" &&
    !unrelatedDiagnostic.Result.ToSafeSummary().Contains("BODY_VALUE_SECRET", StringComparison.Ordinal));

var mimeDiagnostic = DiagnosticValidationFixtures.ValidateDetailedResponse(HttpStatusCode.OK, "text/html", "<html>not-json</html>", diagnosticSession);
Test("diagnostic pinpoints unexpected MIME separately from HTTP success",
    !mimeDiagnostic.Result.IsValid && mimeDiagnostic.Result.Condition == "response_mime_not_json" &&
    mimeDiagnostic.Result.HttpStatus == 200);

var invalidJsonDiagnostic = DiagnosticValidationFixtures.ValidateDetailedResponse(HttpStatusCode.OK, "application/json", "{broken-secretless", diagnosticSession);
Test("diagnostic identifies invalid JSON structure",
    !invalidJsonDiagnostic.Result.IsValid && invalidJsonDiagnostic.Result.Condition == "response_body_invalid_json" &&
    invalidJsonDiagnostic.Result.ResponseSummary == "non_json_text");

var missingIdentityHandler = new DiagnosticValidatorHandler(HttpStatusCode.OK, "application/json", "{\"data\":{\"used\":1,\"free\":2}}");
using (var missingIdentityHttp = new HttpClient(missingIdentityHandler))
{
    var missingIdentityResult = new O2SessionValidator(missingIdentityHttp, "https://cloud.o2online.es/sapi/")
        .ValidateDetailed(diagnosticSession with { DeviceId = "" });
    Test("diagnostic explains missing DeviceId before HTTP without a request",
        !missingIdentityResult.IsValid && missingIdentityResult.Condition == "device_id_missing" &&
        !missingIdentityResult.HttpSendAttempted && missingIdentityHandler.RequestCount == 0);
}

var exceptionDiagnostic = DiagnosticValidationFixtures.ValidateDetailedException(diagnosticSession);
var exceptionSummary = exceptionDiagnostic.ToSafeSummary();
Test("diagnostic captures primary and inner exception types while omitting their messages",
    exceptionDiagnostic.Condition == "http_send_exception" &&
    exceptionDiagnostic.PrimaryExceptionType == nameof(HttpRequestException) &&
    exceptionDiagnostic.InnerExceptionType == nameof(IOException) &&
    !exceptionSummary.Contains("EXCEPTION_MESSAGE_SECRET", StringComparison.Ordinal) &&
    !exceptionSummary.Contains("VALIDATION_SECRET", StringComparison.Ordinal) &&
    !exceptionSummary.Contains("COOKIE_SECRET", StringComparison.Ordinal));

Console.WriteLine($"Login flow tests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class ValidatorResponseHandler(HttpStatusCode statusCode, string mediaType, string body) : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => MakeResponse();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(MakeResponse());

    private HttpResponseMessage MakeResponse() => new(statusCode)
    {
        Content = new StringContent(body)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(mediaType) },
        },
    };
}

static class DiagnosticValidationFixtures
{
    public static (O2SessionValidationDiagnostic Result, int RequestCount) ValidateDetailedResponse(
        HttpStatusCode statusCode, string mediaType, string body, O2Session session)
    {
        using var handler = new DiagnosticValidatorHandler(statusCode, mediaType, body);
        using var http = new HttpClient(handler);
        var result = new O2SessionValidator(http, "https://cloud.o2online.es/sapi/").ValidateDetailed(session);
        return (result, handler.RequestCount);
    }

    public static O2SessionValidationDiagnostic ValidateDetailedException(O2Session session)
    {
        using var handler = new DiagnosticValidatorHandler(new HttpRequestException(
            "EXCEPTION_MESSAGE_SECRET", new IOException("COOKIE_SECRET")));
        using var http = new HttpClient(handler);
        return new O2SessionValidator(http, "https://cloud.o2online.es/sapi/").ValidateDetailed(session);
    }
}

sealed class DiagnosticValidatorHandler : HttpMessageHandler
{
    private readonly HttpStatusCode? _statusCode;
    private readonly string? _mediaType;
    private readonly string? _body;
    private readonly Exception? _exception;
    public int RequestCount { get; private set; }

    public DiagnosticValidatorHandler(HttpStatusCode statusCode, string mediaType, string body)
    {
        _statusCode = statusCode;
        _mediaType = mediaType;
        _body = body;
    }

    public DiagnosticValidatorHandler(Exception exception) => _exception = exception;

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (_exception is not null) throw _exception;
        var response = new HttpResponseMessage(_statusCode!.Value)
        {
            Content = new StringContent(_body!, System.Text.Encoding.UTF8),
            RequestMessage = request,
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(_mediaType!);
        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));
}
