using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;
using O2CloudDrive.VirtualFileSystem;

var results = new List<(string Name, bool Passed)>();
void Test(string name, Action action)
{
    try
    {
        action();
        results.Add((name, true));
        Console.WriteLine("PASS " + name);
    }
    catch (Exception exception)
    {
        results.Add((name, false));
        Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
    }
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

void ThrowsUnauthorized(Action action)
{
    try { action(); }
    catch (UnauthorizedAccessException) { return; }
    throw new InvalidOperationException("expected read-only rejection");
}

var fixtureHandler = new PhotoApiFixtureHandler();
using (var http = new HttpClient(fixtureHandler))
{
    var api = new O2CloudApiClient(http, new FixtureAuth(), "https://cloud.o2online.es/sapi/");
    var first = api.ListPhotoPage(0, 2);
    var second = api.ListPhotoPage(first.NextOffset, 2);

    Test("API filters non-picture media and preserves the observed date field", () =>
    {
        Check(first.Photos.Count == 1 && first.Photos[0].Id == "1001", "first page photo mapping differs");
        Check(first.Photos[0].Date == DateTimeOffset.FromUnixTimeMilliseconds(1706933106000), "date mapping differs");
        Check(first.Photos[0].Size == 1_234 && first.Photos[0].Name == "fixture.HEIC", "detail projection differs");
        Check(first.NextOffset == 2 && first.HasMore, "first page continuation differs");
        Check(second.Photos.Single().Id == "1002" && second.NextOffset == 3 && !second.HasMore, "second page termination differs");
    });

    Test("API uses only the observed read endpoints and offset pagination", () =>
    {
        var listRequests = fixtureHandler.Requests.Where(request => request.Method == "GET").ToArray();
        Check(listRequests.Length == 2, "expected two GET pages");
        Check(listRequests[0].Query["action"] == "get" && listRequests[0].Query["origin"] == "omh" &&
              listRequests[0].Query["limit"] == "2" && listRequests[0].Query["offset"] == "0", "first query differs");
        Check(listRequests[1].Query["offset"] == "2", "second offset differs");
        Check(fixtureHandler.Requests.All(request => request.Path == "/sapi/media" &&
              (request.Method == "GET" || request.Method == "POST") && request.Query.GetValueOrDefault("action") == "get"),
            "unexpected endpoint or mutating action");
        var detailRequests = fixtureHandler.Requests.Where(request => request.Method == "POST").ToArray();
        Check(detailRequests.Length == 2, "expected one metadata projection per page");
        Check(detailRequests.All(request => request.Body is not null &&
              request.Body.Contains("\"size\"", StringComparison.Ordinal) &&
              !request.Body.Contains("\"url\"", StringComparison.Ordinal)), "details requested download URLs or omitted size");
    });
}

var tempRoot = Path.Combine(Path.GetTempPath(), "O2CloudDrive-photo-library-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
try
{
    var cloudApi = new FakePhotoApi();
    var store = new O2CloudFileStore(
        cloudApi,
        new ReadCacheOptions(Path.Combine(tempRoot, "read-cache"), 262144, 0, 1_000_000),
        Path.Combine(tempRoot, "write-cache"));

    Test("root exposes the synthetic read-only Fotos directory without replacing the normal tree", () =>
    {
        Check(store.TryGetByPath("\\Fotos", out var photos) && photos.IsVirtualPhotoRoot, "Fotos root missing");
        Check(store.TryGetByPath("\\normal.txt", out var normal) && !normal.IsInVirtualPhotoTree, "normal root item missing");
        Check((photos.Attributes & 1) != 0, "Fotos root is not marked read-only");
    });

    var photoRoot = store.GetChildren(store.Root).Single(node => node.Name == "Fotos");
    var photoChildren = store.GetChildren(photoRoot);
    Test("photo metadata is paged, deduplicated, and cached without downloading image bytes", () =>
    {
        Check(cloudApi.PhotoPageOffsets.SequenceEqual([0, 100]), "page offsets differ");
        Check(photoChildren.Count == 3 && photoChildren.Select(node => node.Name).ToHashSet().SetEquals(["2024", "2025", "Sin fecha"]),
            "year or undated grouping differs");
        Check(cloudApi.DownloadCalls == 0, "listing downloaded photo content");
        _ = store.GetChildren(photoRoot);
        Check(cloudApi.PhotoPageOffsets.Count == 2, "cached metadata was fetched again");
    });

    Check(store.TryGetByPath("\\Fotos\\2024\\02\\20240203_040506_1001.img", out var knownPhoto), "known fixture photo name differs");
    Check(store.TryGetByPath("\\Fotos\\2025\\01\\20250102_030405_22.heic", out _), "extension from original name was not retained");
    Check(store.TryGetByPath("\\Fotos\\2025\\01\\20250102_030405_55.heic", out _), "extension from MIME metadata differs");
    Check(store.TryGetByPath("\\Fotos\\Sin fecha\\sin-fecha_33.img", out _), "undated stable name differs");
    Check(store.TryGetByPath("\\Fotos\\Sin fecha\\sin-fecha_44.img", out var unknownSize), "unknown-size photo name differs");
    Test("stable synthetic names use UTC date, remote ID, and neutral extensions when unknown", () =>
    {
        Check(knownPhoto.PhotoMetadata?.Id == "1001" && knownPhoto.Size == 1_234, "fixture photo metadata mapping differs");
        Check((knownPhoto.Attributes & 1) != 0 && knownPhoto.IsInVirtualPhotoTree, "photo file is not read-only");
        Check(unknownSize.Size == 0, "unknown-size photo representation differs");
    });

    Test("unknown-size photo reads fail instead of silently returning an empty file", () =>
        ThrowsIOException(() => store.ReadBytes(unknownSize, 0, 1)));

    Test("photo bytes are read on demand using the fixture media ID and requested range", () =>
    {
        Check(store.ReadBytes(knownPhoto, 1, 3).SequenceEqual(new byte[] { 2, 3, 4 }), "range bytes differ");
        Check(cloudApi.DownloadCalls == 1 && cloudApi.LastDownloadId == "1001" &&
              cloudApi.LastDownloadOffset == 1 && cloudApi.LastDownloadLength == 3, "download identity or range differs");
    });

    Test("store rejects create, delete, rename, move-in, write, overwrite, and resize in Fotos", () =>
    {
        ThrowsUnauthorized(() => store.Create("\\Fotos\\2024\\02\\new.img", CloudItemKind.File, 0));
        ThrowsUnauthorized(() => store.Delete(knownPhoto));
        ThrowsUnauthorized(() => store.Rename(knownPhoto, "\\Fotos\\2024\\02\\renamed.img", false));
        Check(store.TryGetByPath("\\normal.txt", out var normal), "normal file missing");
        ThrowsUnauthorized(() => store.Rename(normal, "\\Fotos\\2024\\02\\moved.txt", false));
        ThrowsUnauthorized(() => store.WriteBytes(knownPhoto, 0, [1], false, false));
        ThrowsUnauthorized(() => store.ReplaceContent(knownPhoto, [1]));
        ThrowsUnauthorized(() => store.SetFileSize(knownPhoto, 1));
        Check(cloudApi.MutationCalls == 0, "store called a mutating remote API");
    });

    var fileSystem = new O2VirtualFileSystem(store, "Photo library tests");
    const int accessDenied = unchecked((int)0xC0000022);
    Test("WinFsp surface allows reads and blocks all write-capable access paths", () =>
    {
        Check(fileSystem.Open(knownPhoto.FullPath, 0, 1, out var fileNode, out var fileDesc, out _, out _) == 0,
            "read-only open failed");
        Check(fileSystem.Open(knownPhoto.FullPath, 0, 2, out _, out _, out _, out _) == accessDenied,
            "FILE_WRITE_DATA open was not denied");
        Check(fileSystem.Open(knownPhoto.FullPath, 0, 0x40000000, out _, out _, out _, out _) == accessDenied,
            "GENERIC_WRITE open was not denied");
        foreach (var writeRight in new uint[] { 0x4, 0x10, 0x100, 0x10000, 0x40000, 0x80000, 0x10000000 })
        {
            Check(fileSystem.Open(knownPhoto.FullPath, 0, writeRight, out _, out _, out _, out _) == accessDenied,
                $"write-capable access 0x{writeRight:X8} was not denied");
        }
        Check(fileSystem.Overwrite(knownPhoto, fileDesc, 0, false, 0, out _) == accessDenied, "overwrite was not denied");
        Check(fileSystem.Write(knownPhoto, fileDesc, IntPtr.Zero, 0, 4, false, false, out _, out _) == accessDenied,
            "write was not denied");
        Check(fileSystem.SetBasicInfo(knownPhoto, fileDesc, 0, 0, 0, 0, 0, out _) == accessDenied,
            "attribute/time mutation was not denied");
        Check(fileSystem.SetFileSize(knownPhoto, fileDesc, 4, false, out _) == accessDenied, "resize was not denied");
        Check(fileSystem.CanDelete(knownPhoto, fileDesc, knownPhoto.FullPath) == accessDenied, "delete probe was not denied");
        Check(fileSystem.SetDelete(knownPhoto, fileDesc, knownPhoto.FullPath, true) == accessDenied, "delete-on-close was not denied");
        Check(fileSystem.Rename(knownPhoto, fileDesc, knownPhoto.FullPath, "\\Fotos\\2024\\02\\renamed.img", false) == accessDenied,
            "rename was not denied");
        Check(fileSystem.Create("\\Fotos\\2024\\02\\new.img", 0, 2, 0, null!, 0, out _, out _, out _, out _) == accessDenied,
            "create was not denied");

        var buffer = Marshal.AllocHGlobal(3);
        try
        {
            Check(fileSystem.Read(knownPhoto, fileDesc, buffer, 1, 3, out var bytesRead) == 0 && bytesRead == 3,
                "WinFsp read did not return the photo range");
            var bytes = new byte[3];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            Check(bytes.SequenceEqual(new byte[] { 2, 3, 4 }), "WinFsp read bytes differ");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        fileSystem.Cleanup(fileNode, fileDesc, knownPhoto.FullPath, 0);
        Check(cloudApi.MutationCalls == 0, "WinFsp write path reached a remote mutation");
    });

    Test("Windows generic-read rights allow open, read, metadata, and a simulated local copy", () =>
    {
        // FILE_GENERIC_READ = READ_CONTROL | FILE_READ_DATA | FILE_READ_EA |
        // FILE_READ_ATTRIBUTES | SYNCHRONIZE. READ_CONTROL is an auxiliary
        // standard right and must not be mistaken for a content mutation.
        const uint fileStreamReadAccess = 0x00120089;
        const uint fileStreamCreateOptions = 0x00000040; // FILE_NON_DIRECTORY_FILE
        byte[] securityDescriptor = [];
        Check(fileSystem.GetSecurityByName(knownPhoto.FullPath, out var attributes, ref securityDescriptor) == 0 &&
              (attributes & 1) != 0,
            "read-only security/attribute lookup failed");
        var status = fileSystem.Open(
            knownPhoto.FullPath,
            fileStreamCreateOptions,
            fileStreamReadAccess,
            out var readNode,
            out var readDesc,
            out _,
            out _);
        Check(status == 0,
            $"Windows generic-read open returned NTSTATUS 0x{unchecked((uint)status):X8}");

        Check(fileSystem.GetFileInfo(readNode, readDesc, out var info) == 0 &&
              info.FileSize == 1_234 && info.LastWriteTime != 0,
            "read-only metadata query failed or returned the wrong size");

        var buffer = Marshal.AllocHGlobal(4);
        byte[] content;
        try
        {
            Check(fileSystem.Read(readNode, readDesc, buffer, 0, 4, out var bytesRead) == 0 && bytesRead == 4,
                "read-only callback did not return the requested bytes");
            content = new byte[4];
            Marshal.Copy(buffer, content, 0, content.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        var copyPath = Path.Combine(tempRoot, "simulated-photo-copy.bin");
        File.WriteAllBytes(copyPath, content);
        Check(File.ReadAllBytes(copyPath).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "simulated copy did not preserve the bytes read from Fotos");
        fileSystem.Cleanup(readNode, readDesc, knownPhoto.FullPath, 0);
        fileSystem.Close(readNode, readDesc);
        Check(cloudApi.MutationCalls == 0, "read-only open/copy simulation made a remote mutation");
    });

    Test("ordinary folder items remain writable outside Fotos", () =>
    {
        Check(store.TryGetByPath("\\normal.txt", out var normal), "normal file missing");
        store.ReplaceContent(normal, Encoding.UTF8.GetBytes("offline-write-control"));
        Check(normal.IsDirty && !normal.IsInVirtualPhotoTree, "normal file write behavior changed");
        Check(cloudApi.MutationCalls == 0, "offline control unexpectedly contacted a write API");
    });

    Test("repeated pagination page stops safely instead of looping", () =>
    {
        var repeatingApi = new FakePhotoApi { RepeatPages = true };
        var repeatingStore = new O2CloudFileStore(
            repeatingApi,
            new ReadCacheOptions(Path.Combine(tempRoot, "repeat-read-cache"), 262144, 0, 1_000_000),
            Path.Combine(tempRoot, "repeat-write-cache"));
        Check(repeatingStore.TryGetByPath("\\Fotos", out var repeatingRoot), "repeated-page photo root missing");
        var listing = repeatingStore.GetChildren(repeatingRoot);
        Check(repeatingApi.PhotoPageOffsets.SequenceEqual([0, 100]) && listing.Single().Name == "O2 Cloud - error de listado.txt",
            "repeated page was not stopped and reported");
        Check(repeatingApi.DownloadCalls == 0 && repeatingApi.MutationCalls == 0, "repeated-page guard performed I/O beyond metadata");
    });
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

Console.WriteLine($"Photo library tests: {results.Count(result => result.Passed)} passed, {results.Count(result => !result.Passed)} failed");
return results.All(result => result.Passed) ? 0 : 1;

void ThrowsIOException(Action action)
{
    try { action(); }
    catch (IOException) { return; }
    throw new InvalidOperationException("expected read failure");
}

sealed class FixtureAuth : IAuthService
{
    private readonly O2Session _session = new()
    {
        ValidationKey = "OFFLINE_FIXTURE_VALIDATION",
        DeviceId = "OFFLINE_FIXTURE_DEVICE",
        UserAgent = "PhotoLibraryTests/1.0",
        Cookies = new Dictionary<string, string> { ["sid"] = "OFFLINE_FIXTURE_COOKIE" },
    };

    public O2Session GetCurrentSession() => _session;
    public bool HasStoredSession() => false;
    public bool HasValidatedSession => true;
    public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false) => _session;
    public void Logout() { }
}

sealed record CapturedRequest(string Method, string Path, Dictionary<string, string> Query, string? Body);

sealed class PhotoApiFixtureHandler : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = ParseQuery(request.RequestUri!.Query);
        var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        Requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri.AbsolutePath, query, body));

        if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/sapi/media")
        {
            var offset = int.Parse(query["offset"], System.Globalization.CultureInfo.InvariantCulture);
            var payload = offset == 0
                ? "{\"data\":{\"media\":[{\"id\":1001,\"date\":1706933106000,\"mediatype\":\"picture\",\"status\":\"active\"},{\"id\":9000,\"date\":1706933106000,\"mediatype\":\"file\",\"status\":\"active\"}],\"more\":true}}"
                : "{\"data\":{\"media\":[{\"id\":1002,\"date\":1706933107000,\"mediatype\":\"picture\",\"status\":\"active\"}],\"more\":false}}";
            return Json(payload);
        }

        if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/sapi/media")
        {
            using var json = JsonDocument.Parse(body ?? "{}");
            var ids = json.RootElement.GetProperty("data").GetProperty("ids").EnumerateArray()
                .Select(value => value.ToString()).ToArray();
            var media = string.Join(',', ids.Select(id => id switch
            {
                "1001" => "{\"id\":1001,\"name\":\"fixture.HEIC\",\"size\":1234,\"origin\":{\"name\":\"omh\"}}",
                "1002" => "{\"id\":1002,\"name\":\"second.jpg\",\"size\":3,\"origin\":{\"name\":\"omh\"}}",
                _ => throw new InvalidOperationException("unexpected fixture id"),
            }));
            return Json($"{{\"data\":{{\"media\":[{media}]}}}}");
        }

        throw new InvalidOperationException("unexpected offline request");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, Encoding.UTF8, "text/javascript"),
    };

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? ""));
}

sealed class FakePhotoApi : IO2CloudApiClient, IO2PhotoLibraryApi
{
    private readonly byte[] _bytes = [1, 2, 3, 4, 5, 6];
    private readonly O2CloudItemDto _normalFile = new("normal-id", "normal.txt", "root", false, 0, DateTimeOffset.UnixEpoch, null, "file", null, null, null);
    private readonly O2PhotoMetadata _known = new("1001", "picture", DateTimeOffset.FromUnixTimeMilliseconds(1706933106000), 1_234, null, null);
    private readonly O2PhotoMetadata _named = new("22", "picture", DateTimeOffset.Parse("2025-01-02T03:04:05Z"), 4, "original.HEIC", null);
    private readonly O2PhotoMetadata _mimeNamed = new("55", "picture", DateTimeOffset.Parse("2025-01-02T03:04:05Z"), 1, null, "image/heic");
    private readonly O2PhotoMetadata _undated = new("33", "picture", null, 2, null, null);
    private readonly O2PhotoMetadata _unknownSize = new("44", "picture", null, null, null, null);

    public List<int> PhotoPageOffsets { get; } = [];
    public bool RepeatPages { get; init; }
    public int DownloadCalls { get; private set; }
    public int MutationCalls { get; private set; }
    public string? LastDownloadId { get; private set; }
    public ulong LastDownloadOffset { get; private set; }
    public uint LastDownloadLength { get; private set; }

    public O2PhotoPage ListPhotoPage(int offset, int limit)
    {
        PhotoPageOffsets.Add(offset);
        if (RepeatPages)
        {
            return new O2PhotoPage([_known], offset + 100, true);
        }

        return offset == 0
            ? new O2PhotoPage([_known, _named, _undated, _mimeNamed], 100, true)
            : new O2PhotoPage([_known, _unknownSize], 120, false);
    }

    public O2CloudItemDto GetRootFolder() => new("root", "", null, true, 0, null, null, null, null, null, null);
    public O2StorageInfo GetStorageInfo() => new(0, 1000, 1000);
    public bool KeepSessionAlive() => true;
    public O2ChangeSummary GetChangesSince(long from) => new(false, from, new HashSet<string>(), new HashSet<string>(), false);
    public IReadOnlyList<O2CloudItemDto> ListFolder(string folderId) => folderId == "root" ? [_normalFile] : [];
    public IReadOnlyList<O2CloudItemDto> ListTrash() => [];

    public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length)
    {
        DownloadCalls++;
        LastDownloadId = item.Id;
        LastDownloadOffset = offset;
        LastDownloadLength = length;
        return _bytes.Skip(checked((int)offset)).Take(checked((int)length)).ToArray();
    }

    public string GetShareLink(O2CloudItemDto item) { MutationCalls++; throw new InvalidOperationException("offline fixture"); }
    public O2CloudItemDto CreateFolder(string parentFolderId, string name) { MutationCalls++; throw new InvalidOperationException("offline fixture"); }
    public O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content) { MutationCalls++; throw new InvalidOperationException("offline fixture"); }
    public O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength) { MutationCalls++; throw new InvalidOperationException("offline fixture"); }
    public void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId) => MutationCalls++;
    public void MoveToTrash(O2CloudItemDto item) => MutationCalls++;
    public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) { MutationCalls++; throw new InvalidOperationException("offline fixture"); }
    public void PermanentlyDelete(O2CloudItemDto item) => MutationCalls++;
}
