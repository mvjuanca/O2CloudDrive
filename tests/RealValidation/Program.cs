using System.Security.Cryptography;
using System.Text;
using System.Net;
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
        if (!args.Contains("--validate") && !args.Contains("--login-only") && !args.Contains("--api-read-probe"))
        {
            Console.WriteLine("Use --api-read-probe for authenticated read-only diagnostics, or --validate for the scoped real test after explicit approval.");
            return 0;
        }
        // No Credential Manager, environment credentials, old profiles, app config, shell registration or updates.
        var work = Path.Combine(AppContext.BaseDirectory, "runs", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var logGate = new object();
        void Log(string text)
        {
            var line = DateTimeOffset.Now.ToString("O") + " " + text;
            lock (logGate)
            {
                File.AppendAllText(Path.Combine(work, "real-validation.log"), line + Environment.NewLine);
                Console.WriteLine(line);
            }
        }
        O2Session? currentSessionForTrace = null;
        var diagnosticPhase = "auth-flow-validator";
        var cookieContainer = new CookieContainer();
        var socketsHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = true,
            CookieContainer = cookieContainer,
            UseProxy = true,
        };
        var traceHandler = new SafeHttpTraceHandler(Log, () => diagnosticPhase, () => currentSessionForTrace, cookieContainer)
        {
            InnerHandler = socketsHandler,
        };
        try
        {
            Application.EnableVisualStyles();
            using var http = new HttpClient(traceHandler) { Timeout = TimeSpan.FromMinutes(5) };
            Log("HTTP_CLIENT timeout=300s_diagnostic_only; production_timeout=infinite; connect_timeout=30s; auto_redirect=true; " +
                "decompression=none; cookies=true; system_proxy=true; tls_custom_validation=false; one_shared_http_client=true");
            var validator = new O2SessionValidator(http, "https://cloud.o2online.es/sapi/");
            var profile = Path.Combine(Path.GetTempPath(), "O2CloudDrive-api-read-test3-" + Guid.NewGuid().ToString("N"));
            try
            {
                O2Session? session;
                using (var form = new O2LoginForm(new Uri("https://cloud.o2online.es/"), validator, profile, Log))
                {
                    form.Shown += (_, _) => Log("HUMAN_LOGIN_WINDOW_OPEN; test3 isolated temporary WebView2 profile; no Credential Manager or old profile read; no login timeout");
                    form.ShowDialog();
                    session = form.CapturedSession;
                }

                if (session is not { IsAuthenticated: true })
                {
                    Log("LOGIN_CANCELLED_OR_CLOSED; no API calls or remote writes");
                    return 2;
                }

                currentSessionForTrace = session;
                diagnosticPhase = "post-login-read-only";
                Log("SESSION_VALIDATOR result=PASS; O2LoginForm only returns CapturedSession after O2SessionValidator.Validate(session) accepts it");
                Log($"SESSION_CAPTURED validationkey_present={!string.IsNullOrWhiteSpace(session.ValidationKey)} " +
                    $"cookie_count={session.Cookies.Count(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))} " +
                    $"cookie_header_present={!string.IsNullOrWhiteSpace(session.CookieHeader)} user_agent_present={!string.IsNullOrWhiteSpace(session.UserAgent)} " +
                    "other_session_fields=none; values_not_logged; profile_retained_through_all_reads");

                var memoryAuth = new MemoryAuth(session);
                var client = new O2CloudApiClient(http, memoryAuth, "https://cloud.o2online.es/sapi/");
                Log($"SESSION_REUSE same_session_object={ReferenceEquals(session, memoryAuth.Session)} same_http_client_as_validator=true");

                var allReadsPassed = true;
                try
                {
                    var keepAlive = client.KeepSessionAlive();
                    Log($"READ_STEP KeepSessionAlive result={(keepAlive ? "PASS" : "FAIL")}");
                    allReadsPassed &= keepAlive;
                }
                catch (Exception ex)
                {
                    allReadsPassed = false;
                    Log("READ_STEP KeepSessionAlive result=FAIL " + SafeHttpTraceHandler.FormatExceptionChain(ex));
                }

                O2StorageInfo? storage = null;
                try
                {
                    storage = client.GetStorageInfo();
                    Log($"READ_STEP GetStorageInfo result=PASS used_bytes={storage.UsedBytes} total_bytes={storage.TotalBytes} free_bytes={storage.FreeBytes}");
                }
                catch (Exception ex)
                {
                    allReadsPassed = false;
                    Log("READ_STEP GetStorageInfo result=FAIL " + SafeHttpTraceHandler.FormatExceptionChain(ex));
                }

                O2CloudItemDto? root = null;
                try
                {
                    root = client.GetRootFolder();
                    Log("READ_STEP GetRootFolder result=PASS root_id_present=true root_name_omitted=true");
                }
                catch (Exception ex)
                {
                    allReadsPassed = false;
                    Log("READ_STEP GetRootFolder result=FAIL " + SafeHttpTraceHandler.FormatExceptionChain(ex));
                }

                if (root is not null)
                {
                    try
                    {
                        var rootItems = client.ListFolder(root.Id);
                        Log($"READ_STEP ListRoot result=PASS item_count={rootItems.Count} names_and_ids_omitted=true");
                    }
                    catch (Exception ex)
                    {
                        allReadsPassed = false;
                        Log("READ_STEP ListRoot result=FAIL " + SafeHttpTraceHandler.FormatExceptionChain(ex));
                    }
                }
                else
                {
                    allReadsPassed = false;
                    Log("READ_STEP ListRoot result=NOT_RUN root_unavailable");
                }

                if (args.Contains("--login-only") || args.Contains("--api-read-probe"))
                {
                    Log($"READ_ONLY_DIAGNOSTIC_COMPLETE all_steps_passed={allReadsPassed}; no mount; no remote writes");
                    return allReadsPassed ? 0 : 4;
                }

                if (!allReadsPassed || storage is null || root is null)
                {
                    Log("BLOCKED: read-only preflight incomplete; refusing all real test writes");
                    return 4;
                }

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
            finally
            {
                if (TryDeleteProfile(profile))
                {
                    Log("TEMP_WEBVIEW_PROFILE_REMOVED after all read-only/API steps; cookies/storage not retained on disk");
                }
                else
                {
                    Log("WARNING: temporary WebView2 profile could not be removed; no further operation attempted");
                }
            }
        }
        catch (Exception ex)
        {
            Log("ABORTED: " + SafeHttpTraceHandler.FormatExceptionChain(ex) + "; no additional remote operation attempted");
            return 1;
        }
    }

    private static bool TryDeleteProfile(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return !Directory.Exists(path);
            }
            catch (IOException) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) { Thread.Sleep(500); }
        }

        return false;
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
    public O2Session Session => session;
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
