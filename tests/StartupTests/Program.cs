using System.Net;
using O2CloudDrive.Auth;
using O2CloudDrive.Config;
using O2CloudDrive.Startup;

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

var config = new ConfigService().Load([]);
Test("existing configured mount point O: is preserved", config.MountPoint == "O:");
Test("automatic mount defaults enabled for the existing install config", config.AutoMountOnStartup);

var autoMount = new AutoMountCoordinator();
var mountCalls = 0;
var validMount = await autoMount.TryMountAsync(true, true, false, () =>
{
    mountCalls++;
    return Task.FromResult(true);
});
var duplicateMount = await autoMount.TryMountAsync(true, true, false, () =>
{
    mountCalls++;
    return Task.FromResult(true);
});
Test("valid stored session requests one automatic mount", validMount.Status == AutoMountStatus.Mounted && mountCalls == 1);
Test("repeated startup/login callback cannot request a second mount", duplicateMount.Status == AutoMountStatus.AlreadyAttempted && mountCalls == 1);

var invalidAutoMount = new AutoMountCoordinator();
var invalidCalls = 0;
var invalidResult = await invalidAutoMount.TryMountAsync(true, false, false, () =>
{
    invalidCalls++;
    return Task.FromResult(true);
});
Test("invalid session never requests a mount", invalidResult.Status == AutoMountStatus.SessionNotValidated && invalidCalls == 0);

var disabledAutoMount = new AutoMountCoordinator();
var disabledResult = await disabledAutoMount.TryMountAsync(false, true, false, () => Task.FromResult(true));
Test("disabled setting leaves manual mount available", disabledResult.Status == AutoMountStatus.Disabled);

var mountedAutoMount = new AutoMountCoordinator();
var mountedCalls = 0;
var mountedResult = await mountedAutoMount.TryMountAsync(true, true, true, () =>
{
    mountedCalls++;
    return Task.FromResult(true);
});
Test("already mounted unit is not mounted twice", mountedResult.Status == AutoMountStatus.AlreadyMounted && mountedCalls == 0);

var failedAutoMount = new AutoMountCoordinator();
var failedResult = await failedAutoMount.TryMountAsync(true, true, false, () => Task.FromResult(false));
var failedRetryResult = await failedAutoMount.TryMountAsync(true, true, false, () => Task.FromResult(true));
Test("mount failure is reported without escaping the UI action", failedResult.Status == AutoMountStatus.Failed && failedResult.ErrorType == "MountOperationFailed");
Test("failed automatic mount is not retried as a duplicate request", failedRetryResult.Status == AutoMountStatus.AlreadyAttempted);

var loginSession = ValidSession() with { ValidationKey = "NEW_OFFLINE_VALIDATION" };
var loginStore = new MemorySessionStore(ValidSession());
var loginService = new FakeLoginService(loginSession);
using (var loginHttp = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, "application/json", "{\"data\":{\"used\":1,\"free\":9}}")))
{
    var auth = new AuthService(loginStore, loginService, new O2SessionValidator(loginHttp, "https://cloud.o2online.es/sapi/"));
    var loggedIn = auth.EnsureAuthenticated(allowInteractive: true, forceInteractive: true);
    var postLoginCoordinator = new AutoMountCoordinator();
    var postLoginCalls = 0;
    var postLoginResult = await postLoginCoordinator.TryMountAsync(true, auth.HasValidatedSession, false, () =>
    {
        postLoginCalls++;
        return Task.FromResult(true);
    });
    Test("successful Login replaces the old session and can trigger auto-mount", loggedIn?.ValidationKey == loginSession.ValidationKey &&
        loginStore.Current?.ValidationKey == loginSession.ValidationKey && loginStore.DeleteCount == 0 && loginStore.SaveCount == 1 &&
        postLoginResult.Status == AutoMountStatus.Mounted && postLoginCalls == 1);
}

var validStore = new MemorySessionStore(ValidSession());
using (var validHttp = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, "application/json", "{\"data\":{\"used\":1,\"free\":9}}")))
{
    var auth = new AuthService(validStore, new FakeLoginService(ValidSession()), new O2SessionValidator(validHttp, "https://cloud.o2online.es/sapi/"));
    var result = auth.ValidateStoredSession();
    Test("valid saved session is Accepted and retained", result.Status == StoredSessionValidationStatus.Valid &&
        result.Classification == SessionValidationClassification.Accepted && validStore.DeleteCount == 0 && auth.HasValidatedSession);
}

var rejectedStore = new MemorySessionStore(ValidSession());
var rejectedHandler = new FixedResponseHandler(HttpStatusCode.Unauthorized, "application/json", "{}");
using (var rejectedHttp = new HttpClient(rejectedHandler))
{
    var auth = new AuthService(rejectedStore, new FakeLoginService(ValidSession()), new O2SessionValidator(rejectedHttp, "https://cloud.o2online.es/sapi/"));
    var result = auth.ValidateStoredSession();
    var secondCheck = auth.EnsureAuthenticated(allowInteractive: false);
    Test("HTTP 401 is classified and preserves the saved evidence", result.Status == StoredSessionValidationStatus.Rejected &&
        result.Classification == SessionValidationClassification.Unauthorized401 && rejectedStore.DeleteCount == 0 &&
        auth.HasStoredSession() && secondCheck is null && rejectedHandler.RequestCount == 1);
}

var forbiddenStore = new MemorySessionStore(ValidSession());
using (var forbiddenHttp = new HttpClient(new FixedResponseHandler(HttpStatusCode.Forbidden, "text/html", "denied")))
{
    var auth = new AuthService(forbiddenStore, new FakeLoginService(ValidSession()), new O2SessionValidator(forbiddenHttp, "https://cloud.o2online.es/sapi/"));
    var result = auth.ValidateStoredSession();
    Test("HTTP 403 is classified and preserves the saved session", result.Classification == SessionValidationClassification.Forbidden403 &&
        forbiddenStore.DeleteCount == 0 && auth.HasStoredSession());
}

var missingKeyStore = new MemorySessionStore(ValidSession() with { ValidationKey = "" });
using (var missingKeyHttp = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, "application/json", "{}")))
{
    var auth = new AuthService(missingKeyStore, new FakeLoginService(ValidSession()), new O2SessionValidator(missingKeyHttp, "https://cloud.o2online.es/sapi/"));
    var result = auth.ValidateStoredSession();
    Test("missing ValidationKey is classified without deleting the stored session", result.Classification == SessionValidationClassification.MissingValidationKey &&
        missingKeyStore.DeleteCount == 0 && missingKeyStore.Current is not null);
}

var missingDeviceStore = new MemorySessionStore(ValidSession() with { DeviceId = "" });
var missingDeviceHandler = new FixedResponseHandler(HttpStatusCode.OK, "application/json", "{}");
using (var missingDeviceHttp = new HttpClient(missingDeviceHandler))
{
    var auth = new AuthService(missingDeviceStore, new FakeLoginService(ValidSession()), new O2SessionValidator(missingDeviceHttp, "https://cloud.o2online.es/sapi/"));
    var result = auth.ValidateStoredSession();
    Test("missing DeviceId is classified without HTTP or credential deletion", result.Classification == SessionValidationClassification.MissingDeviceId &&
        missingDeviceStore.DeleteCount == 0 && missingDeviceHandler.RequestCount == 0 && auth.HasStoredSession());
}

var unavailableStore = new MemorySessionStore(ValidSession());
var unavailableLogin = new FakeLoginService(ValidSession());
using (var unavailableHttp = new HttpClient(new FixedResponseHandler(HttpStatusCode.ServiceUnavailable, "application/json", "{}")))
{
    var auth = new AuthService(unavailableStore, unavailableLogin, new O2SessionValidator(unavailableHttp, "https://cloud.o2online.es/sapi/"));
    var result = auth.ValidateStoredSession();
    var interactiveAttempt = auth.EnsureAuthenticated(allowInteractive: true);
    Test("transient HTTP failure preserves the saved session and does not prompt for login", result.Status == StoredSessionValidationStatus.Unavailable &&
        result.Classification == SessionValidationClassification.TransientNetworkFailure && interactiveAttempt is null &&
        unavailableStore.DeleteCount == 0 && unavailableLogin.Calls == 0 && auth.HasStoredSession());
}

var networkStore = new MemorySessionStore(ValidSession());
using (var networkHttp = new HttpClient(new ThrowingHandler()))
{
    var auth = new AuthService(networkStore, new FakeLoginService(ValidSession()), new O2SessionValidator(networkHttp, "https://cloud.o2online.es/sapi/"));
    var result = auth.ValidateStoredSession();
    Test("network exception is not treated as session expiry", result.Status == StoredSessionValidationStatus.Unavailable &&
        result.Classification == SessionValidationClassification.TransientNetworkFailure && networkStore.DeleteCount == 0 && auth.HasStoredSession());
}

var timeoutStore = new MemorySessionStore(ValidSession());
using (var timeoutHttp = new HttpClient(new TimeoutHandler()))
{
    var auth = new AuthService(timeoutStore, new FakeLoginService(ValidSession()), new O2SessionValidator(
        timeoutHttp, "https://cloud.o2online.es/sapi/", requestTimeout: TimeSpan.FromMilliseconds(25)));
    var result = auth.ValidateStoredSession();
    Test("timeout is classified and preserves the saved session", result.Classification == SessionValidationClassification.Timeout &&
        timeoutStore.DeleteCount == 0 && auth.HasStoredSession());
}

var failedLoginStore = new MemorySessionStore(ValidSession());
using (var failedLoginHttp = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, "application/json", "{}")))
{
    var auth = new AuthService(failedLoginStore, new FakeLoginService(null), new O2SessionValidator(failedLoginHttp, "https://cloud.o2online.es/sapi/"));
    var failedLogin = auth.EnsureAuthenticated(allowInteractive: true, forceInteractive: true);
    Test("cancelled or unsuccessful forced Login leaves the previous credential intact", failedLogin is null &&
        failedLoginStore.DeleteCount == 0 && failedLoginStore.Current?.ValidationKey == ValidSession().ValidationKey);
}

var logoutStore = new MemorySessionStore(ValidSession());
var logoutService = new FakeLoginService(ValidSession());
using (var logoutHttp = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, "application/json", "{}")))
{
    var auth = new AuthService(logoutStore, logoutService, new O2SessionValidator(logoutHttp, "https://cloud.o2online.es/sapi/"));
    auth.Logout();
    Test("explicit Logout is the supported session deletion path", logoutStore.DeleteCount == 1 &&
        logoutStore.Current is null && logoutService.ClearSessionCacheCount == 1);
}

var mutexName = "Local\\O2CloudDrive.StartupTests." + Guid.NewGuid().ToString("N");
var singleInstancePass = false;
using (var first = SingleInstanceGuard.TryAcquire(mutexName))
{
    var secondWasBlocked = Task.Run(() =>
    {
        using var second = SingleInstanceGuard.TryAcquire(mutexName);
        return second is null;
    }).GetAwaiter().GetResult();
    singleInstancePass = first is not null && secondWasBlocked;
}
using var acquiredAfterRelease = SingleInstanceGuard.TryAcquire(mutexName);
Test("single-instance guard prevents a second process and releases cleanly", singleInstancePass && acquiredAfterRelease is not null);

Console.WriteLine($"Startup tests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static O2Session ValidSession() => new()
{
    ValidationKey = "OFFLINE_VALIDATION_ONLY",
    DeviceId = "OFFLINE_DEVICE_ONLY",
    UserAgent = "StartupTests/1.0",
    Cookies = new Dictionary<string, string> { ["sid"] = "OFFLINE_COOKIE_ONLY" },
};

sealed class MemorySessionStore(O2Session? session = null) : ISessionStore
{
    private O2Session? _session = session;
    public int DeleteCount { get; private set; }
    public int SaveCount { get; private set; }
    public O2Session? Current => _session;
    public O2Session? TryRead() => _session;
    public void Save(O2Session value) { SaveCount++; _session = value; }
    public void Delete() { DeleteCount++; _session = null; }
}

sealed class FakeLoginService(O2Session? session) : IInteractiveLoginService
{
    public int Calls { get; private set; }
    public int ClearSessionCacheCount { get; private set; }
    public O2Session? Login(O2SessionValidator validator) { Calls++; return session; }
    public void ClearSessionCache() { ClearSessionCacheCount++; }
}

sealed class FixedResponseHandler(HttpStatusCode status, string mediaType, string body) : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) { RequestCount++; return Response(); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { RequestCount++; return Task.FromResult(Response()); }
    private HttpResponseMessage Response() => new(status)
    {
        Content = new StringContent(body)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) },
        },
    };
}

sealed class TimeoutHandler : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("unreachable");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

sealed class ThrowingHandler : HttpMessageHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("OFFLINE_TEST_FAILURE");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("OFFLINE_TEST_FAILURE");
}
