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
    Path.Combine(root, "writes"));
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
    var fs = new O2VirtualFileSystem(store, "test"); store.ReplaceContent(n, next);
    for (int i = 0; i < 3; i++) { Check(fs.Flush(n, new object(), out _) == 0, "flush failed"); fs.Cleanup(n, new object(), "\\control.txt", 0); }
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
            return Seed(candidate, memory.ToArray());
        }
    }
    void CheckWait() { if (!UploadRelease.Wait(5000)) throw new IOException("test upload wait timeout"); }
    public void RenameOrMove(O2CloudItemDto item, string name, string parent) { lock (gate) {
        if (name == "control.txt" && item.Name != "control.txt") Maybe("promote");
        if (files.Values.Any(x => x.Item.Id != item.Id && x.Item.Name == name)) throw new IOException("already exists");
        var entry = files[item.Id]; files[item.Id] = (entry.Item with { Name = name, ParentId = parent }, entry.Bytes);
    } }
    public void MoveToTrash(O2CloudItemDto item) { lock (gate) { Maybe("trash"); trash[item.Id] = files[item.Id]; files.Remove(item.Id); } }
    public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) { lock (gate) { files[item.Id] = trash[item.Id]; trash.Remove(item.Id); return files[item.Id].Item; } }
    public void PermanentlyDelete(O2CloudItemDto item) { lock (gate) { files.Remove(item.Id); trash.Remove(item.Id); } }
}
