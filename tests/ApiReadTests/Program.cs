using System.Net;
using System.Text.Json;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;

var passed = 0;
var failed = 0;
void Test(string name, bool condition)
{
    if (condition) { passed++; Console.WriteLine("PASS " + name); }
    else { failed++; Console.WriteLine("FAIL " + name); }
}
// All identities and payloads below are local fixtures, never real account values.
var session = new O2Session
{
    ValidationKey = "FAKE_VALIDATION", DeviceId = "FAKE_BROWSER_DEVICE", UserAgent = "Fixture/1.0",
    Cookies = new Dictionary<string, string> { ["sid"] = "FAKE_COOKIE" },
};
using var handler = new FixtureHandler();
using var http = new HttpClient(handler);
var client = new O2CloudApiClient(http, new FakeAuth(session), "https://cloud.o2online.es/sapi/");
var root = client.GetRootFolder();
Test("root is retrieved with the observed GET signature", root.Id == "1" && handler.Requests[0].Method == "GET");
var items = client.ListFolder("1");
Test("text/javascript folder and media JSON is parsed", items.Count == 0);
var folder = handler.Requests.Single(r => r.Path == "/sapi/media/folder");
var media = handler.Requests.Single(r => r.Path == "/sapi/media");
Test("folder query uses only observed names", folder.Query.Keys.ToHashSet().SetEquals(["action", "parentid", "limit", "validationkey"]));
Test("media query uses only observed names", media.Query.Keys.ToHashSet().SetEquals(["action", "folderid", "limit", "validationkey"]));
Test("GET omits Origin", folder.Origin is null);
Test("POST retains the observed Origin", media.Origin == "https://cloud.o2online.es");
Test("all reads use captured device identity", handler.Requests.All(r => r.DeviceId == session.DeviceId));
Test("all reads emit exactly one device identity header", handler.Requests.All(r => r.DeviceIdHeaderCount == 1));
Test("all reads preserve session cookies and User-Agent", handler.Requests.All(r => r.Cookie == "sid=FAKE_COOKIE" && r.UserAgent == session.UserAgent));
Test("GET retains observed form Content-Type without a body", folder.ContentType == "application/x-www-form-urlencoded" && folder.Body == "");
Test("POST retains JSON Content-Type and a projection body", media.ContentType == "application/json" && JsonDocument.Parse(media.Body!).RootElement.GetProperty("data").GetProperty("fields").ValueKind == JsonValueKind.Array);
Test("no Authorization invented", handler.Requests.All(r => !r.Authorization));

var fixtureDownloadBytes = System.Text.Encoding.UTF8.GetBytes("diagnostic-download-fixture");
var testDownloadItem = new O2CloudItemDto("2", "fixture.mp4", "1", false, fixtureDownloadBytes.Length,
    DateTimeOffset.UnixEpoch, "https://cloud.o2online.es/sapi/download/file?action=get&k=fixture&node=fixture",
    "video", null, null, null);
var downloadedFixture = client.DownloadFile(testDownloadItem, 0, checked((uint)fixtureDownloadBytes.Length));
Test("download retrieves the expected ranged bytes", downloadedFixture.SequenceEqual(fixtureDownloadBytes));
var rangedRequests = handler.Requests.Where(r => r.Path == "/sapi/download/file").ToArray();
Test("range probe and download both use captured DeviceId", rangedRequests.Length >= 2 && rangedRequests.All(r => r.DeviceId == session.DeviceId));
Test("range probe and download emit one identity and omit GET Origin", rangedRequests.Length >= 2 && rangedRequests.All(r => r.DeviceIdHeaderCount == 1 && r.Origin is null));

var validator = new O2SessionValidator(http, "https://cloud.o2online.es/sapi/");
Test("strict validator accepts authenticated quota JSON in JavaScript MIME", validator.Validate(session));
var quota = handler.Requests.Last();
Test("validator uses real device identity and no GET Origin", quota.DeviceId == session.DeviceId && quota.Origin is null);
var count = handler.Requests.Count;
Test("validator rejects missing device identity without sending", !validator.Validate(session with { DeviceId = "" }) && handler.Requests.Count == count);
var missingClient = new O2CloudApiClient(http, new FakeAuth(session with { DeviceId = "" }), "https://cloud.o2online.es/sapi/");
bool missingRejected;
try { missingClient.GetRootFolder(); missingRejected = false; } catch (InvalidOperationException) { missingRejected = true; }
Test("client does not invent a device identity when missing", missingRejected && handler.Requests.Count == count);
bool missingUploadRejected;
try { missingClient.UploadFile("1", "fixture.txt", new byte[] { 1 }); missingUploadRejected = false; }
catch (InvalidOperationException) { missingUploadRejected = true; }
Test("upload rejects missing DeviceId before transport", missingUploadRejected && handler.Requests.Count == count);
var serialized = JsonSerializer.Serialize(session);
Test("session can round-trip captured device identity", JsonSerializer.Deserialize<O2Session>(serialized)?.DeviceId == session.DeviceId);

using var mutationHandler = new FixtureHandler(HttpStatusCode.Unauthorized);
using var mutationHttp = new HttpClient(mutationHandler);
var mutationClient = new O2CloudApiClient(mutationHttp, new FakeAuth(session), "https://cloud.o2online.es/sapi/");
void CheckAuthenticatedMutation(string name, Action action)
{
    var start = mutationHandler.Requests.Count;
    try { action(); } catch { }
    var sent = mutationHandler.Requests.Skip(start).ToArray();
    Test(name, sent.Length > 0 && sent.All(r => r.DeviceId == session.DeviceId && r.DeviceIdHeaderCount == 1));
}
CheckAuthenticatedMutation("upload uses one captured DeviceId", () => mutationClient.UploadFile("1", "upload-fixture.txt", new byte[] { 1, 2, 3 }));
Exception? sanitizedUploadFailure = null;
try { mutationClient.UploadFile("1", "error-fixture.txt", new byte[] { 4, 5, 6 }); }
catch (Exception error) { sanitizedUploadFailure = error; }
Test("HTTP upload failure preserves status with body markers but no raw response text",
    sanitizedUploadFailure is IOException && sanitizedUploadFailure.Message.Contains("HTTP 401", StringComparison.Ordinal) &&
    sanitizedUploadFailure.Message.Contains("body_values_omitted=true", StringComparison.Ordinal) &&
    !sanitizedUploadFailure.Message.Contains("login required", StringComparison.Ordinal));
CheckAuthenticatedMutation("create-folder fallback requests use captured DeviceId", () => mutationClient.CreateFolder("1", "folder-fixture"));
var mutationItem = new O2CloudItemDto("3", "mutation-fixture.txt", "1", false, 3,
    DateTimeOffset.UnixEpoch, null, "file", null, null, null);
CheckAuthenticatedMutation("rename/move candidates use captured DeviceId", () => mutationClient.RenameOrMove(mutationItem, "renamed-fixture.txt", "1"));
CheckAuthenticatedMutation("soft-delete candidates use captured DeviceId", () => mutationClient.MoveToTrash(mutationItem));
CheckAuthenticatedMutation("restore candidates use captured DeviceId", () => mutationClient.RestoreFromTrash(mutationItem));
CheckAuthenticatedMutation("permanent-delete candidates use captured DeviceId", () => mutationClient.PermanentlyDelete(mutationItem));
Test("no authenticated API operation emitted duplicate DeviceId values", mutationHandler.Requests.All(r => r.DeviceIdHeaderCount == 1));

using var missingIdentityMutationHandler = new FixtureHandler();
using var missingIdentityMutationHttp = new HttpClient(missingIdentityMutationHandler);
var missingIdentityClient = new O2CloudApiClient(
    missingIdentityMutationHttp,
    new FakeAuth(session with { DeviceId = "" }),
    "https://cloud.o2online.es/sapi/");
void CheckMissingIdentityMutation(string name, Action action)
{
    var before = missingIdentityMutationHandler.Requests.Count;
    var rejected = false;
    try { action(); } catch (InvalidOperationException) { rejected = true; }
    Test(name, rejected && missingIdentityMutationHandler.Requests.Count == before);
}
CheckMissingIdentityMutation("create-folder fails before transport without a DeviceId", () => missingIdentityClient.CreateFolder("1", "folder-fixture"));
CheckMissingIdentityMutation("rename fails before transport without a DeviceId", () => missingIdentityClient.RenameOrMove(mutationItem, "renamed-fixture.txt", "1"));
CheckMissingIdentityMutation("trash fails before transport without a DeviceId", () => missingIdentityClient.MoveToTrash(mutationItem));
CheckMissingIdentityMutation("restore fails before transport without a DeviceId", () => missingIdentityClient.RestoreFromTrash(mutationItem));
CheckMissingIdentityMutation("permanent delete fails before transport without a DeviceId", () => missingIdentityClient.PermanentlyDelete(mutationItem));
Console.WriteLine($"API read tests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeAuth(O2Session session) : IAuthService
{
    public O2Session GetCurrentSession() => session;
    public bool HasStoredSession() => false;
    public bool HasValidatedSession => true;
    public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false) => session;
    public void Logout() { }
}
sealed record CapturedRequest(string Method, string Path, Dictionary<string,string> Query, string? DeviceId, int DeviceIdHeaderCount,
    string? Origin, string? Cookie, string? UserAgent, string? ContentType, string? Body, bool Authorization);
sealed class FixtureHandler(HttpStatusCode? statusOverride = null) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=',2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.First() : null;
        var deviceIds = request.Headers.TryGetValues("X-Deviceid", out var values) ? values.ToArray() : Array.Empty<string>();
        Requests.Add(new(request.Method.Method, request.RequestUri.AbsolutePath, query, deviceIds.FirstOrDefault(), deviceIds.Length,
            Header("Origin"), Header("Cookie"), Header("User-Agent"), request.Content?.Headers.ContentType?.MediaType,
            request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult(), request.Headers.Contains("Authorization")));
        if (statusOverride is { } status)
        {
            return new HttpResponseMessage(status) { Content = new StringContent("<html>login required</html>", System.Text.Encoding.UTF8, "text/html") };
        }
        if (request.Method == HttpMethod.Get && request.Headers.Range is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("diagnostic-download-fixture")),
            };
        }

        var payload = request.RequestUri.AbsolutePath switch
        {
            "/sapi/media/folder/root" => "{\"data\":{\"folders\":[{\"id\":1,\"name\":\"Fixture\"}]}}",
            "/sapi/media/folder" => "{\"data\":{\"folders\":[]}}",
            _ when query["action"] == "get-storage-space" => "{\"data\":{\"used\":2,\"free\":8}}",
            _ => "{\"data\":{\"media\":[],\"more\":false}}",
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/javascript") };
    }
}
