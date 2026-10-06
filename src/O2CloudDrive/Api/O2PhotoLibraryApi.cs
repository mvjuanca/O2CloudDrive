namespace O2CloudDrive.Api;

public sealed record O2PhotoMetadata(
    string Id,
    string MediaType,
    DateTimeOffset? Date,
    long? Size,
    string? Name,
    string? MimeType);

public sealed record O2PhotoPage(
    IReadOnlyList<O2PhotoMetadata> Photos,
    int NextOffset,
    bool HasMore);

public interface IO2PhotoLibraryApi
{
    O2PhotoPage ListPhotoPage(int offset, int limit);
}
