using System.Security.Cryptography;
using System.Text;
using Fsp;
using O2CloudDrive.Api;
using O2CloudDrive.Auth;
using O2CloudDrive.VirtualFileSystem;

internal static class Program
{
    private const string FolderName = "O2CloudDrive-Overwrite-Test-20261004";
    [STAThread]
    static int Main(string[] args)
    {
        if (!args.Contains("--validate") && !args.Contains("--login-only"))
        {
            Console.WriteLine("Use --login-only for fresh human login, or --validate for the scoped real test after login.");
            return 0;
        }
        // No Credential Manager, environment credentials, old profiles, app config, shell registration or updates.
        var work = Path.Combine(AppContext.BaseDirectory, "runs", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        void Log(string text)
        {
            var line = DateTimeOffset.Now.ToString("O") + " " + text;
            File.AppendAllText(Path.Combine(work, "real-validation.log"), line + Environment.NewLine);
            Console.WriteLine(line);
        }
        try
        {
            Application.EnableVisualStyles();
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var validator = new O2SessionValidator(http, "https://cloud.o2online.es/sapi/");
            using var form = new O2LoginForm(new Uri("https://cloud.o2online.es/"), validator, Path.Combine(work, "fresh-webview"));
            using var timeout = new System.Windows.Forms.Timer { Interval = 90000 };
            timeout.Tick += (_, _) => { Log("BLOCKED: human login not completed within 90 seconds; no remote writes"); form.Close(); };
            form.Shown += (_, _) => { Log("HUMAN_LOGIN_WINDOW_OPEN; fresh isolated profile; no saved credentials read"); timeout.Start(); };
            form.ShowDialog(); timeout.Stop();
            if (form.CapturedSession is not { IsAuthenticated: true } session)
            {
                Log("TODO EL TRABAJO OFFLINE COMPLETADO; FALTA LOGIN HUMANO PARA VALIDACIÓN REAL");
                return 2;
            }
            Log("Fresh human login validated; session is memory-only, never logged");
            if (args.Contains("--login-only")) return 0;
            var client = new O2CloudApiClient(http, new MemoryAuth(session), "https://cloud.o2online.es/sapi/");
            var root = client.GetRootFolder();
            var folders = client.ListFolder(root.Id).Where(x => x.Name == FolderName).ToArray();
            if (folders.Length > 1 || folders.Any(x => !x.IsFolder)) throw new IOException("Ambiguous test folder");
            var folder = folders.SingleOrDefault() ?? client.CreateFolder(root.Id, FolderName);
            if (client.ListFolder(folder.Id).Any(x => x.Name == "overwrite-control.txt" || x.Name == "renamed-control.txt"))
                throw new IOException("Prior evidence exists; refusing to overwrite an earlier run");
            var scoped = new ScopedApi(client, folder);
            var cache = new ReadCacheOptions(Path.Combine(work, "cache"), 262144, 0, 10000000);
            O2CloudFileStore NewStore() => new(scoped, cache, Path.Combine(work, "writes"));
            var store = NewStore();
            using var host = Mount(store);
            var path = "R:\\overwrite-control.txt";
            void Verify(string name, byte[] expected, string label)
            {
                var items = scoped.ListFolder(folder.Id).Where(x => !x.IsFolder).ToArray();
                if (items.Length != 1 || items[0].Name != name || items[0].Size != expected.Length)
                    throw new IOException("Remote count/name/size did not match");
                var item = items[0];
                var bytes = expected.Length == 0 ? [] : scoped.DownloadFile(item, 0, (uint)expected.Length);
                if (!bytes.SequenceEqual(expected) || !File.ReadAllBytes(path).SequenceEqual(expected))
                    throw new IOException("Remote/local content did not match");
                Log($"PASS {label}; id={item.Id}; name={item.Name}; bytes={item.Size}; sha256={Convert.ToHexString(SHA256.HashData(bytes))}; objects=1; suffix=false");
            }
            var final = Array.Empty<byte>();
            foreach (var version in new[] { "V1", "V2", "V3" })
            {
                final = Encoding.UTF8.GetBytes("O2 overwrite control " + version);
                File.WriteAllBytes(path, final);
                Verify("overwrite-control.txt", final, version);
            }
            File.Move(path, "R:\\renamed-control.txt"); path = "R:\\renamed-control.txt";
            Verify("renamed-control.txt", final, "rename");
            foreach (var bytes in new[] { Enumerable.Repeat((byte)65, 2048).ToArray(), "short"u8.ToArray(), Array.Empty<byte>(), "FINAL-V3"u8.ToArray() })
            {
                File.WriteAllBytes(path, bytes); final = bytes; Verify("renamed-control.txt", bytes, "size/reopen");
            }
            host.Unmount();
            using var remounted = Mount(NewStore());
            Verify("renamed-control.txt", final, "fresh store and real remount");
            remounted.Unmount();
            Log("REAL VALIDATION PASS; small evidence folder preserved; no writes outside test folder");
            return 0;
        }
        catch (Exception ex)
        {
            // API exception bodies/URLs might include session information. Never print them.
            Log("ABORTED: " + ex.GetType().Name + "; preserve test folder and local transaction journals; no additional operation attempted");
            return 1;
        }
    }

    private static FileSystemHost Mount(ICloudFileStore store)
    {
        if (Directory.Exists("R:\\")) throw new IOException("R: already in use; refusing to mount");
        var host = new FileSystemHost(new O2VirtualFileSystem(store, "O2 scoped TEST"))
        {
            FileSystemName = "O2Test", SectorSize = 512, SectorsPerAllocationUnit = 8,
            CaseSensitiveSearch = false, CasePreservedNames = true, UnicodeOnDisk = true,
            PersistentAcls = false, PostCleanupWhenModifiedOnly = false,
            PassQueryDirectoryPattern = true, FlushAndPurgeOnCleanup = true,
        };
        if (host.Mount("R:", null, false, 0) != 0) { host.Dispose(); throw new IOException("WinFsp mount failed"); }
        return host;
    }
}

internal sealed class MemoryAuth(O2Session session) : IAuthService
{
    public O2Session GetCurrentSession() => session;
    public bool HasStoredSession() => false;
    public bool HasValidatedSession => true;
    public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false) => session;
    public void Logout() { }
}

// The store sees the test folder as its root. Every mutation/download is checked by ID.
internal sealed class ScopedApi(IO2CloudApiClient inner, O2CloudItemDto folder) : IO2CloudApiClient
{
    void Parent(string id) { if (id != folder.Id) throw new UnauthorizedAccessException("Outside test scope"); }
    void Item(O2CloudItemDto item) { Parent(item.ParentId ?? ""); if (!inner.ListFolder(folder.Id).Any(x => x.Id == item.Id)) throw new UnauthorizedAccessException("Unknown scoped object"); }
    public O2CloudItemDto GetRootFolder() => folder;
    public O2StorageInfo GetStorageInfo() => inner.GetStorageInfo();
    public bool KeepSessionAlive() => true;
    public O2ChangeSummary GetChangesSince(long from) => new(false, from, new HashSet<string>(), new HashSet<string>(), false);
    public IReadOnlyList<O2CloudItemDto> ListFolder(string id) { Parent(id); return inner.ListFolder(id); }
    public IReadOnlyList<O2CloudItemDto> ListTrash() => [];
    public byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length) { Item(item); return inner.DownloadFile(item, offset, length); }
    public string GetShareLink(O2CloudItemDto item) => throw new NotSupportedException();
    public O2CloudItemDto CreateFolder(string parent, string name) => throw new NotSupportedException();
    public O2CloudItemDto UploadFile(string parent, string name, byte[] content) { Parent(parent); return inner.UploadFile(parent, name, content); }
    public O2CloudItemDto UploadFile(string parent, string name, Stream content, long size) { Parent(parent); return inner.UploadFile(parent, name, content, size); }
    public void RenameOrMove(O2CloudItemDto item, string name, string parent) { Parent(parent); Item(item); inner.RenameOrMove(item, name, parent); }
    public void MoveToTrash(O2CloudItemDto item) { Item(item); inner.MoveToTrash(item); }
    public O2CloudItemDto RestoreFromTrash(O2CloudItemDto item) => throw new NotSupportedException();
    public void PermanentlyDelete(O2CloudItemDto item) => throw new NotSupportedException();
}
