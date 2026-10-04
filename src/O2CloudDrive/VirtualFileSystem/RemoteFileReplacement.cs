using System.Security.Cryptography;
using System.Text.Json;
using O2CloudDrive.Api;

namespace O2CloudDrive.VirtualFileSystem;

// No WinFsp/UI dependency. Only capabilities already exposed by IO2CloudApiClient.
internal sealed class RemoteFileReplacement(IO2CloudApiClient api, string journalDirectory)
{
    internal O2CloudItemDto Execute(O2CloudItemDto? original, string parentId, string name, Stream content, long size)
    {
        EnsureNoUnfinishedTransaction(parentId, name);
        var token = Guid.NewGuid().ToString("N");
        var temporary = $".o2cd-{token}.upload{Path.GetExtension(name)}";
        var backup = $".o2cd-{token}.backup{Path.GetExtension(name)}";
        var journal = Path.Combine(journalDirectory, token + ".json");
        string phase = "preflight";
        O2CloudItemDto? uploaded = null;
        var hash = HashLocal(content, size);
        void Record(string nextPhase)
        {
            phase = nextPhase;
            // Deliberately do not serialize DTOs: they can contain download tokens/URLs.
            var data = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, time = DateTimeOffset.UtcNow, phase, parentId, name, size, hash,
                oldId = original?.Id, newId = uploaded?.Id, temporary, backup,
                localRetryPath = (content as FileStream)?.Name,
            });
            Directory.CreateDirectory(journalDirectory);
            using (var output = new FileStream(journal + ".next", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(data);
                output.Flush(flushToDisk: true);
            }
            File.Move(journal + ".next", journal, overwrite: true);
            Trace(nextPhase, original?.Id, uploaded?.Id, name, size);
        }
        try
        {
            var initial = api.ListFolder(parentId);
            var named = initial.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (original is null ? named.Length != 0 :
                named.Length != 1 || named[0].Id != original.Id || named[0].Size != original.Size ||
                !SameVersion(named[0], original))
                throw new IOException("Remote target changed; refusing replacement.");
            Record("upload");
            content.Position = 0;
            uploaded = api.UploadFile(parentId, temporary, content, size);
            Record("verify-upload");
            uploaded = Require(parentId, uploaded.Id, temporary, size);
            if (!HashRemote(uploaded).Equals(hash, StringComparison.Ordinal))
                throw new IOException("Remote upload content verification failed.");

            if (original is not null)
            {
                // Recheck the original after upload (external clients are not covered by our lock).
                var current = Require(parentId, original.Id, name, original.Size);
                if (!SameVersion(current, original))
                    throw new IOException("Original changed while uploading; refusing replacement.");
                Record("rename-original");
                api.RenameOrMove(original, backup, parentId);
                Require(parentId, original.Id, backup, original.Size);
            }
            Record("promote");
            api.RenameOrMove(uploaded, name, parentId);
            var promoted = Require(parentId, uploaded.Id, name, size);
            // Verify again before retiring the only old remote copy.
            if (!HashRemote(promoted).Equals(hash, StringComparison.Ordinal))
                throw new IOException("Promoted content verification failed.");
            if (original is not null)
            {
                Record("retire-original");
                api.MoveToTrash(original with { Name = backup });
                if (api.ListFolder(parentId).Any(x => x.Id == original.Id))
                    throw new IOException("Old object retirement was not confirmed.");
            }
            Record("completed");
            return promoted;
        }
        catch (Exception error)
        {
            var failedPhase = phase;
            // A failed HTTP response can follow a successful server mutation.
            // Inspect actual IDs/names before any rollback; never delete a valid copy.
            bool recovered = false;
            O2CloudItemDto? liveNew = null;
            O2CloudItemDto? rollbackCurrent = null;
            try
            {
                Record("rollback");
                var items = api.ListFolder(parentId);
                var liveOld = original is null ? null : items.SingleOrDefault(x => x.Id == original.Id);
                liveNew = uploaded is null ? null : items.SingleOrDefault(x => x.Id == uploaded.Id);
                if (original is not null && liveOld is null)
                    throw new IOException("Original is no longer in the active folder; manual recovery required.");
                if (liveNew?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                {
                    api.RenameOrMove(liveNew, temporary, parentId);
                    Require(parentId, liveNew.Id, temporary, size);
                    liveNew = liveNew with { Name = temporary };
                }
                if (liveOld is not null && liveOld.Name == backup)
                {
                    api.RenameOrMove(liveOld, name, parentId);
                    Require(parentId, liveOld.Id, name, original!.Size);
                }
                if (original is not null)
                    rollbackCurrent = Require(parentId, original.Id, name, original.Size);
                else if (api.ListFolder(parentId).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("New target has ambiguous ownership.");
                recovered = true;
                Record("rolled-back");
            }
            catch
            {
                // Preserve remote artifacts, local retry bytes and the durable journal.
                try { Record("recovery-required"); } catch { /* earlier durable record remains */ }
            }
            Trace(recovered ? "failed-rolled-back" : "recovery-required", original?.Id, uploaded?.Id, name, size);
            throw new RemoteReplacementException(
                $"O2 Cloud replacement failed ({failedPhase}); {(recovered ? "original preserved; retry possible" : "automatic retry blocked; recovery required")}. Journal: {journal}",
                // HTTP/API exception bodies may contain credentials. Never log them via WinFsp.
                new IOException($"Remote failure type: {error.GetType().Name}"), recoveryRequired: !recovered,
                recovered ? rollbackCurrent : liveNew?.Name == name ? liveNew : null);
        }
    }

    private void EnsureNoUnfinishedTransaction(string parentId, string name)
    {
        if (!Directory.Exists(journalDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(journalDirectory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = document.RootElement;
                if (root.GetProperty("parentId").GetString() != parentId ||
                    !string.Equals(root.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (root.GetProperty("phase").GetString() is "completed" or "rolled-back") continue;
            }
            catch (JsonException)
            {
                throw new IOException("O2 Cloud transaction journal is corrupt; preserve it and recover before writing.");
            }
            throw new IOException($"O2 Cloud unfinished replacement requires recovery before retry. Journal: {path}");
        }
    }

    private static bool SameVersion(O2CloudItemDto current, O2CloudItemDto original)
        => (string.IsNullOrEmpty(original.Fingerprint) || current.Fingerprint == original.Fingerprint) &&
           (original.ModifiedAt is null || current.ModifiedAt == original.ModifiedAt);

    private O2CloudItemDto Require(string parentId, string id, string name, long size)
    {
        var items = api.ListFolder(parentId);
        var matches = items.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Id != id || matches[0].IsFolder || matches[0].Size != size)
            throw new IOException("Remote name/ID/size confirmation failed.");
        return matches[0];
    }

    private string HashRemote(O2CloudItemDto item)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < item.Size;)
        {
            var count = (uint)Math.Min(1024 * 1024, item.Size - offset);
            var bytes = api.DownloadFile(item, (ulong)offset, count);
            if (bytes.Length == 0 || bytes.Length > count)
                throw new IOException("Remote verification read returned an invalid length.");
            digest.AppendData(bytes);
            offset += bytes.Length;
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static string HashLocal(Stream content, long size)
    {
        if (!content.CanSeek || content.Length != size)
            throw new IOException("Committed content size changed.");
        content.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(content));
        content.Position = 0;
        return hash;
    }

    private static void Trace(string phase, string? oldId, string? newId, string name, long size)
    {
        if (Environment.GetEnvironmentVariable("O2CLOUD_TRACE_WRITES") == "1")
            Console.Error.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, phase, name, oldId, newId, size }));
    }
}

internal sealed class RemoteReplacementException(string message, Exception inner, bool recoveryRequired, O2CloudItemDto? currentItem)
    : IOException(message, inner)
{
    internal bool RecoveryRequired { get; } = recoveryRequired;
    internal O2CloudItemDto? CurrentItem { get; } = currentItem;
}
