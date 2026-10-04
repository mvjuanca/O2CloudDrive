using O2CloudDrive.Auth;
using System.Net;

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

var exceptionLog = SafeHttpTraceHandler.FormatExceptionChain(new HttpRequestException(
    "Request failed at https://cloud.o2online.es/sapi/media?action=get&validationkey=QUERY_SECRET&phone=PHONE_SECRET",
    new IOException("pin=PIN_SECRET password=PASSWORD_SECRET"), HttpStatusCode.Forbidden));
Test("HTTP exception chain includes status/type but redacts URL and credential values",
    exceptionLog.Contains("HttpRequestException", StringComparison.Ordinal) &&
    exceptionLog.Contains("Forbidden", StringComparison.Ordinal) &&
    exceptionLog.Contains("IOException", StringComparison.Ordinal) &&
    !exceptionLog.Contains("QUERY_SECRET", StringComparison.Ordinal) &&
    !exceptionLog.Contains("PHONE_SECRET", StringComparison.Ordinal) &&
    !exceptionLog.Contains("PIN_SECRET", StringComparison.Ordinal) &&
    !exceptionLog.Contains("PASSWORD_SECRET", StringComparison.Ordinal));

var traceLines = new List<string>();
var traceCookies = new CookieContainer();
using (var traceHandler = new SafeHttpTraceHandler(traceLines.Add, () => "test", () => new O2Session
{
    ValidationKey = "SESSION_SECRET",
    UserAgent = "SafeTest/1.0",
    Cookies = new Dictionary<string, string> { ["sid"] = "COOKIE_SECRET" },
}, traceCookies) { InnerHandler = new FixedResponseHandler() })
using (var traceClient = new HttpClient(traceHandler))
using (var traceRequest = new HttpRequestMessage(HttpMethod.Get,
           "https://cloud.o2online.es/sapi/media?action=get-storage-space&validationkey=QUERY_SECRET"))
{
    traceRequest.Headers.TryAddWithoutValidation("Cookie", "sid=COOKIE_SECRET");
    traceRequest.Headers.TryAddWithoutValidation("User-Agent", "SafeTest/1.0");
    using var traceResponse = traceClient.SendAsync(traceRequest).GetAwaiter().GetResult();
    using var syncRequest = new HttpRequestMessage(HttpMethod.Get,
        "https://cloud.o2online.es/sapi/profile/role?action=get&validationkey=QUERY_SECRET");
    syncRequest.Headers.TryAddWithoutValidation("Cookie", "sid=COOKIE_SECRET");
    syncRequest.Headers.TryAddWithoutValidation("User-Agent", "SafeTest/1.0");
    using var syncResponse = traceClient.Send(syncRequest);
}
var traceOutput = string.Join('\n', traceLines);
Test("HTTP diagnostics report endpoint/status/header presence without sensitive values",
    traceOutput.Contains("method=GET", StringComparison.Ordinal) &&
    traceOutput.Contains("/sapi/media", StringComparison.Ordinal) &&
    traceOutput.Contains("validationkey_present=True", StringComparison.Ordinal) &&
    traceOutput.Contains("cookie_header_present=True", StringComparison.Ordinal) &&
    traceOutput.Contains("status=403", StringComparison.Ordinal) &&
    traceLines.Count(line => line.StartsWith("HTTP_REQUEST ", StringComparison.Ordinal)) == 2 &&
    traceLines.Count(line => line.StartsWith("HTTP_RESPONSE ", StringComparison.Ordinal)) == 2 &&
    traceOutput.Contains("content_type=application/json", StringComparison.Ordinal) &&
    !traceOutput.Contains("SESSION_SECRET", StringComparison.Ordinal) &&
    !traceOutput.Contains("COOKIE_SECRET", StringComparison.Ordinal) &&
    !traceOutput.Contains("QUERY_SECRET", StringComparison.Ordinal));

var runner = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealValidation.Program.cs"));
Test("human login runner has no fixed 90-second close", !runner.Contains("90000", StringComparison.Ordinal) && !runner.Contains("System.Windows.Forms.Timer", StringComparison.Ordinal));

Console.WriteLine($"Login flow tests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FixedResponseHandler : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => MakeResponse(request);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(MakeResponse(request));
    }

    private static HttpResponseMessage MakeResponse(HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            ReasonPhrase = "Forbidden",
            Content = new StringContent("{\"error\":\"redacted-test-body\"}", System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
        return response;
    }
}
