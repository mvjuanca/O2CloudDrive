namespace O2CloudDrive.Api;

public interface IO2CloudApiClient
{
    O2CloudItemDto GetRootFolder();
    O2StorageInfo GetStorageInfo();
    bool KeepSessionAlive();
    O2ChangeSummary GetChangesSince(long from);
    IReadOnlyList<O2CloudItemDto> ListFolder(string folderId);
    IReadOnlyList<O2CloudItemDto> ListTrash();
    byte[] DownloadFile(O2CloudItemDto item, ulong offset, uint length);
    string GetShareLink(O2CloudItemDto item);
    O2CloudItemDto CreateFolder(string parentFolderId, string name);
    O2CloudItemDto UploadFile(string parentFolderId, string name, byte[] content);
    O2CloudItemDto UploadFile(string parentFolderId, string name, Stream content, long contentLength);
    void RenameOrMove(O2CloudItemDto item, string newName, string parentFolderId);
    void MoveToTrash(O2CloudItemDto item);
    O2CloudItemDto RestoreFromTrash(O2CloudItemDto item);
    void PermanentlyDelete(O2CloudItemDto item);
}
