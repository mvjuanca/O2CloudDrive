using System.Text;
using System.Security.Cryptography;
using O2CloudDrive.Api;
using O2CloudDrive.VirtualFileSystem;

var root = Path.Combine(Path.GetTempPath(), "O2CloudDrive-overwrite-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<(string Name, bool Passed)>();
var reproduce = args.Contains("--reproduce");

O2CloudFileStore Store(FakeApi api) => new(api,
    new ReadCacheOptions(Path.Combine(root, Guid.NewGuid().ToString("N")), 262144, 0, 10000000),
    Path.Combine(root, "writes", api.CacheId));
CloudNode Node(O2CloudFileStore store, string name = "control.txt")
{
    Check(store.TryGetByPath("\\" + name, out var node), "node missing");
    return node;
}
void Commit(O2CloudFileStore store, CloudNode node)
{
    store.Commit(node);
    Check(SpinWait.SpinUntil(() => !node.UploadInProgress, 10000), "commit did not finish");
}
void Remote(FakeApi api, string name, byte[] expected)
{
    var items = api.ListFolder("root").Where(x => x.Name == name).ToArray();
    Check(items.Length == 1, "canonical remote name must be unique");
    Check(api.Bytes(items[0].Id).SequenceEqual(expected), "remote bytes differ");
    Check(!api.ListFolder("root").Any(x => x.Name.Contains("(1)")), "suffix duplicate");
}
void Test(string name, Action action)
{
    try { action(); results.Add((name, true)); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add((name, false)); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
byte[] V(string value) => Encoding.UTF8.GetBytes(value);
var old = V("CONTROL_VERSION_1");
var next = V("CONTROL_VERSION_2"); // same size deliberately catches stale confirmation

#if !CORE_ONLY
if (args.Contains("--mounted"))
{
    const string mount = "R:";
    Check(!Directory.Exists(mount + "\\"), "R: is already in use; refusing mount");
    var api = new FakeApi(); var store = Store(api);
    using var host = new Fsp.FileSystemHost(new O2VirtualFileSystem(store, "O2 overwrite OFFLINE"))
    {
        FileSystemName = "O2Offline", SectorSize = 512, SectorsPerAllocationUnit = 8,
        CaseSensitiveSearch = false, CasePreservedNames = true, UnicodeOnDisk = true,
        PersistentAcls = false, PostCleanupWhenModifiedOnly = false,
        PassQueryDirectoryPattern = true, FlushAndPurgeOnCleanup = true,
    };
    Check(host.Mount(mount, null, false, 0) == 0, "WinFsp mount failed");
    var path = mount + "\\control.txt";
    void Saved(byte[] bytes) { File.WriteAllBytes(path, bytes); Check(File.ReadAllBytes(path).SequenceEqual(bytes), "mounted read differs"); Remote(api, "control.txt", bytes); }
    try
    {
        Test("mounted A new", () => Saved(old));
        Test("mounted B overwrite", () => Saved(next));
        Test("mounted C repeated", () => { for (int i = 0; i < 3; i++) Saved(V("V" + i)); });
        Test("mounted D grow", () => Saved(Enumerable.Repeat((byte)7, 500000).ToArray()));
        Test("mounted E shrink", () => Saved(V("small")));
        Test("mounted F zero", () => Saved([]));
        Test("mounted G rename after overwrite", () => { Saved(next); File.Move(path, mount + "\\renamed.txt"); path = mount + "\\renamed.txt"; Remote(api, "renamed.txt", next); });
        Test("mounted H overwrite after rename", () => { File.WriteAllBytes(path, old); Remote(api, "renamed.txt", old); });
        Test("mounted I repeated flush", () => { using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); stream.Write(next); stream.SetLength(next.Length); for (int i = 0; i < 3; i++) stream.Flush(true); Remote(api, "renamed.txt", next); });
        Test("mounted J reopen", () => Check(File.ReadAllBytes(path).SequenceEqual(next), "mounted reopen stale"));
        Test("mounted L explicit Flush remote error", () => {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Write(old); api.Fail("upload"); ExpectIOException(() => stream.Flush(true));
        });
    }
    finally { host.Unmount(); }
    Console.WriteLine($"MOUNTED TOTAL {results.Count(x => x.Passed)}/{results.Count} PASS; R: unmounted");
    return results.All(x => x.Passed) ? 0 : 1;
}
#endif

if (reproduce)
{
    var api = new FakeApi(); api.Seed("control.txt", old); api.Fail("trash");
    var store = Store(api); var node = Node(store);
    store.ReplaceContent(node, next); Commit(store, node);
    var listing = api.ListFolder("root");
    foreach (var item in listing)
        Console.WriteLine($"REMOTE {item.Id} {item.Name} size={item.Size} sha256={Convert.ToHexString(SHA256.HashData(api.Bytes(item.Id)))}");
    var demonstrated = listing.Any(x => x.Name == "control.txt" && api.Bytes(x.Id).SequenceEqual(old))
        && listing.Any(x => x.Name == "control (1).txt" && api.Bytes(x.Id).SequenceEqual(next));
    Console.WriteLine($"REPRODUCED={demonstrated}; dirty={node.IsDirty}; localRead={Encoding.UTF8.GetString(store.ReadAllBytes(node))}");
    return demonstrated ? 0 : 1;
}

Test("A new file", () => {
    var api = new FakeApi(); var store = Store(api);
    var n = store.Create("\\new.txt", CloudItemKind.File, 0);
    store.ReplaceContent(n, next); Commit(store, n); Remote(api, "new.txt", next);
});
Test("B same-path overwrite", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); Commit(store, n); Remote(api, "control.txt", next);
    Check(store.ReadAllBytes(n).SequenceEqual(next), "local read stale");
});
Test("C three overwrites", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    for (int i = 0; i < 3; i++) { var bytes = V("version-" + i); store.ReplaceContent(n, bytes); Commit(store, n); Remote(api, "control.txt", bytes); }
    Check(api.ListFolder("root").Count == 1, "temporary leftovers on success");
});
foreach (var (name, size) in new[] { ("D grow", 400000), ("E shrink", 2), ("F zero", 0) })
    Test(name, () => {
        var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
        store.SetFileSize(n, (ulong)size); var bytes = store.ReadAllBytes(n); Commit(store, n);
        Remote(api, "control.txt", bytes); Check(n.Size == (ulong)size, "size mismatch");
    });
Test("G rename after overwrite", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); Commit(store, n); store.Rename(n, "\\renamed.txt", false); Remote(api, "renamed.txt", next);
});
Test("H overwrite after rename", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.Rename(n, "\\renamed.txt", false); store.ReplaceContent(n, next); Commit(store, n); Remote(api, "renamed.txt", next);
});
Test("I repeated Flush/Cleanup", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next);
#if CORE_ONLY
    for (int i = 0; i < 3; i++) Commit(store, n);
#else
    var fs = new O2VirtualFileSystem(store, "test");
    for (int i = 0; i < 3; i++) { Check(fs.Flush(n, new object(), out _) == 0, "flush failed"); fs.Cleanup(n, new object(), "\\control.txt", 0); }
#endif
    Check(SpinWait.SpinUntil(() => !n.UploadInProgress, 10000), "cleanup pending");
    Remote(api, "control.txt", next); Check(api.UploadCount == 1, "double upload");
});
Test("J close/reopen fresh store", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); Commit(store, n); var reopened = Store(api);
    Check(reopened.ReadAllBytes(Node(reopened)).SequenceEqual(next), "reopen stale");
});
Test("K two nearby commits", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); Parallel.Invoke(() => Commit(store, n), () => Commit(store, n));
    Check(api.UploadCount == 1, "duplicate commits"); Remote(api, "control.txt", next);
});
foreach (var (name, failure) in new[] { ("L upload error", "upload"), ("M retirement error", "trash"), ("N promotion rename error", "promote"), ("O confirmation error", "confirm") })
    Test(name, () => {
        var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
        store.ReplaceContent(n, next); api.Fail(failure);
        ExpectIOException(() => Commit(store, n)); Check(n.IsDirty, "failure must preserve dirty state");
        Remote(api, "control.txt", old); Check(store.ReadAllBytes(n).SequenceEqual(next), "retry bytes lost");
        api.ClearFailures(); Commit(store, n); Remote(api, "control.txt", next);
    });
Test("P stale block cache", () => {
    var api = new FakeApi(); var initial = Enumerable.Repeat((byte)1, 700000).ToArray(); var updated = Enumerable.Repeat((byte)2, 700000).ToArray();
    api.Seed("control.txt", initial); var store = Store(api); var n = Node(store);
    Check(store.ReadBytes(n, 0, 300000).All(x => x == 1), "initial cache");
    store.ReplaceContent(n, updated); Commit(store, n);
    Check(store.ReadBytes(n, 0, 300000).All(x => x == 2), "stale cached blocks"); Remote(api, "control.txt", updated);
});
Test("Q remote ID changes", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store); var oldId = n.Id;
    store.ReplaceContent(n, next); Commit(store, n); Check(n.Id != oldId, "test requires new ID");
    Check(n.Id == api.ListFolder("root").Single().Id, "metadata points at wrong ID"); Remote(api, "control.txt", next);
});
Test("R unrelated file remains usable during upload", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); api.Seed("other.txt", old); var store = Store(api); var n = Node(store); var other = Node(store, "other.txt");
    store.ReplaceContent(n, next); api.BlockUpload = true;
    var task = Task.Run(() => Commit(store, n)); Check(api.UploadStarted.Wait(2000), "upload not started");
    try { var read = Task.Run(() => store.ReadAllBytes(other)); Check(read.Wait(1000), "global lock held over upload"); }
    finally { api.UploadRelease.Set(); }
    task.GetAwaiter().GetResult(); Remote(api, "control.txt", next);
});
Test("S write races with commit", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store); store.ReplaceContent(n, next); api.BlockUpload = true;
    var commit = Task.Run(() => Commit(store, n)); Check(api.UploadStarted.Wait(2000), "upload not started");
    var write = Task.Run(() => store.ReplaceContent(n, V("V3")));
    Thread.Sleep(100); api.UploadRelease.Set(); Task.WaitAll(commit, write);
    Commit(store, n); Remote(api, "control.txt", V("V3"));
});
Test("T corrupt uploaded bytes keep original", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); api.CorruptUpload = true;
    ExpectIOException(() => Commit(store, n)); Remote(api, "control.txt", old); Check(n.IsDirty, "retry lost");
});
Test("U lost response after original rename rolls back", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); api.Fail("backup-after");
    ExpectIOException(() => Commit(store, n)); Remote(api, "control.txt", old);
});
Test("V rollback failure blocks mutations and restart", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); api.Fail("promote"); api.Fail("restore");
    ExpectIOException(() => Commit(store, n));
    ExpectIOException(() => store.ReplaceContent(n, V("V3")));
    Check(api.ListFolder("root").Any(x => api.Bytes(x.Id).SequenceEqual(old)), "last old copy lost");
    var fresh = Store(api); var created = fresh.Create("\\control.txt", CloudItemKind.File, 0);
    fresh.ReplaceContent(created, next); var count = api.UploadCount;
    ExpectIOException(() => Commit(fresh, created)); Check(api.UploadCount == count, "restart replayed unfinished transaction");
});
Test("W lost retirement response preserves verified new copy", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); api.Fail("trash-after");
    ExpectIOException(() => Commit(store, n)); Remote(api, "control.txt", next);
    ExpectIOException(() => Commit(store, n)); Check(api.ListTrash().Count == 1, "old copy not in trash");
});
Test("X Flush exposes remote failure", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    store.ReplaceContent(n, next); api.Fail("upload");
#if CORE_ONLY
    ExpectIOException(() => Commit(store, n));
#else
    var fs = new O2VirtualFileSystem(store, "test");
    ExpectIOException(() => fs.Flush(n, new object(), out _));
#endif
    Check(n.IsDirty, "flush lost dirty state");
});
#if !CORE_ONLY
Test("Y read-only Cleanup does not commit open writer", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    var fs = new O2VirtualFileSystem(store, "test");
    Check(fs.Open("\\control.txt", 0, 1, out var fn, out var fd, out _, out _) == 0, "read open failed");
    store.ReplaceContent(n, next); fs.Cleanup(fn, fd, "\\control.txt", 0);
    Check(api.UploadCount == 0, "read-only close committed writer"); Commit(store, n); Remote(api, "control.txt", next);
});
Test("Z two writers commit on last Cleanup only", () => {
    var api = new FakeApi(); api.Seed("control.txt", old); var store = Store(api); var n = Node(store);
    var fs = new O2VirtualFileSystem(store, "test");
    Check(fs.Open("\\control.txt", 0, 2, out var fn, out var first, out _, out _) == 0, "open1");
    Check(fs.Open("\\control.txt", 0, 2, out _, out var second, out _, out _) == 0, "open2");
    store.ReplaceContent(n, next); fs.Cleanup(fn, first, "\\control.txt", 0);
    Check(api.UploadCount == 0, "first close committed unfinished writer");
    fs.Cleanup(fn, second, "\\control.txt", 0); fs.Cleanup(fn, second, "\\control.txt", 0);
    Check(api.UploadCount == 1, "last close duplicated upload"); Remote(api, "control.txt", next);
});
Test("AA actual upload request creates without existing ID", () => {
    using var handler = new UploadInspector(); using var http = new HttpClient(handler);
    var client = new O2CloudApiClient(http, new FakeAuth(), "https://example.invalid/sapi/");
    ExpectIOException(() => client.UploadFile("123", "control.txt", next));
    Check(handler.Checked, "actual multipart contract not inspected");
});
#endif
Console.WriteLine($"TOTAL {results.Count(x => x.Passed)}/{results.Count} PASS; isolated-cache={root}");
return results.All(x => x.Passed) ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void ExpectIOException(Action action) {
    try { action(); } catch (IOException) { return; }
    throw new Exception("error was silently reported as success");
}

// O2-like collision handling and explicit failure injection, never a real HTTP request.
sealed class FakeApi : IO2CloudApiClient
{
    readonly object gate = new();
    readonly Dictionary<string, (O2CloudItemDto Item, byte[] Bytes)> files = new();
    readonly Dictionary<string, (O2CloudItemDto Item, byte[] Bytes)> trash = new();
    readonly HashSet<string> failures = new();
    int serial; public int UploadCount;
    public string CacheId { get; } = Guid.NewGuid().ToString("N");
    public bool CorruptUpload;
    public bool BlockUpload;
    public ManualResetEventSlim UploadStarted = new(false), UploadRelease = new(false);
    public void Fail(string phase) { lock (gate) failures.Add(phase); }
    public void ClearFailures() { lock (gate) failures.Clear(); }
    void Maybe(string phase) { if (failures.Remove(phase)) throw new IOException("injected " + phase); }
    public O2CloudItemDto Seed(string name, byte[] bytes) {
        lock (gate) { var id = "file-" + ++serial; var item = new O2CloudItemDto(id, name, "root", false, bytes.Length, DateTimeOffset.UtcNow, null, "file", id, null, null); files[id] = (item, bytes.ToArray()); return item; }
    }
    public byte[] Bytes(string id) { lock (gate) return files[id].Bytes.ToArray(); }
    public O2CloudItemDto GetRootFolder() => new("root", "", null, true, 0, null, null, null, null, null, null);
    public O2StorageInfo GetStorageInfo() => new(0, 1000000000, 1000000000);
    public bool KeepSessionAlive() => true;
    public O2ChangeSummary GetChangesSince(long from) => new(false, from, new HashSet<string>(), new HashSet<string>(), false);
    public IReadOnlyList<O2CloudItemDto> ListFolder(string id) { lock (gate) { if (UploadCount > 0) Maybe("confirm"); return files.Values.Select(x => x.Item).ToArray(); } }
    public IReadOnlyList<O2CloudItemDto> ListTrash() { lock (gate) return trash.Values.Select(x => x.Item).ToArray(); }
    public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length) { lock (gate) return files[item.Id].Bytes.Skip(checked((int)offset)).Take(checked((int)length)).ToArray(); }
    public string GetShareLink(O2CloudItemDto item) => "https://example.invalid/" + item.Id;
    public O2CloudItemDto CreateFolder(string parent, string name) => throw new NotSupportedException();
    public O2CloudItemDto UploadFile(string parent, string name, byte[] bytes) { using var stream = new MemoryStream(bytes); return UploadFile(parent, name, stream, bytes.Length); }
    public O2CloudItemDto UploadFile(string parent, string name, Stream stream, long size) {
        Interlocked.Increment(ref UploadCount); UploadStarted.Set(); if (BlockUpload) CheckWait();
        lock (gate) {
            Maybe("upload"); using var memory = new MemoryStream(); stream.CopyTo(memory);
            var candidate = name; int i = 0;
            while (files.Values.Any(x => x.Item.Name == candidate)) candidate = Path.GetFileNameWithoutExtension(name) + " (" + ++i + ")" + Path.GetExtension(name);
            var bytes = memory.ToArray(); if (CorruptUpload && bytes.Length > 0) bytes[0] ^= 255;
            return Seed(candidate, bytes);
        }
    }
    void CheckWait() { if (!UploadRelease.Wait(5000)) throw new IOException("test upload wait timeout"); }
    public void RenameOrMove(O2CloudItemDto item, string name, string parent) { lock (gate) {
        if (name == "control.txt" && item.Name.Contains(".upload")) Maybe("promote");
        if (name == "control.txt" && item.Name.Contains(".backup")) Maybe("restore");
        if (files.Values.Any(x => x.Item.Id != item.Id && x.Item.Name == name)) throw new IOException("already exists");
        var entry = files[item.Id]; files[item.Id] = (entry.Item with { Name = name, ParentId = parent }, entry.Bytes);
        if (name.Contains(".backup")) Maybe("backup-after");
    } }
    public void MoveToTrash(O2CloudItemDto item) { lock (gate) { Maybe("trash"); trash[item.Id] = files[item.Id]; files.Remove(item.Id); Maybe("trash-after"); } }
    public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) { lock (gate) { files[item.Id] = trash[item.Id]; trash.Remove(item.Id); return files[item.Id].Item; } }
    public void PermanentlyDelete(O2CloudItemDto item) { lock (gate) { files.Remove(item.Id); trash.Remove(item.Id); } }
}

#if !CORE_ONLY
sealed class UploadInspector : HttpMessageHandler
{
    public bool Checked;
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/sapi/upload" ||
            !request.RequestUri.Query.StartsWith("?action=save", StringComparison.Ordinal)) throw new Exception("unexpected upload endpoint");
        var multipart = request.Content as MultipartFormDataContent ?? throw new Exception("not multipart");
        var data = multipart.Single(x => x.Headers.ContentDisposition?.Name?.Trim('"') == "data");
        using var json = System.Text.Json.JsonDocument.Parse(await data.ReadAsStringAsync(cancellationToken));
        var metadata = json.RootElement.GetProperty("data");
        if (metadata.GetProperty("name").GetString() != "control.txt" || metadata.GetProperty("size").GetInt32() != 17 ||
            metadata.TryGetProperty("id", out _)) throw new Exception("unexpected upload metadata");
        Checked = true;
        throw new IOException("intentional offline transport stop after inspecting request");
    }
}
sealed class FakeAuth : O2CloudDrive.Auth.IAuthService
{
    public O2CloudDrive.Auth.O2Session GetCurrentSession() => new() { ValidationKey = "FAKE-NOT-A-CREDENTIAL" };
    public bool HasStoredSession() => false;
    public bool HasValidatedSession => true;
    public O2CloudDrive.Auth.O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false) => GetCurrentSession();
    public void Logout() { }
}
#endif
