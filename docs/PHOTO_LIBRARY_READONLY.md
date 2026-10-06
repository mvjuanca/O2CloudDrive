# O2 virtual photo library

`O:\Fotos` is a synthetic, read-only view of O2's photo collection. It is
separate from the ordinary `media/folder/root` namespace and does not move or
copy server objects.

## Metadata and layout

The API enumerates the `omh` origin through the observed read-only
`/sapi/media?action=get` route, using offset pagination. Photo metadata is
deduplicated and cached for the mounted session; repeated pages and
non-advancing offsets stop enumeration rather than looping. Listing metadata
does not download image bodies.

The API's `date` field determines the virtual year/month grouping. It is not
asserted to be the camera capture time. Items without that field appear under
`Sin fecha`. Stable synthetic file names use the UTC API date and remote media
ID, with an extension taken from the original name or MIME type when available;
unknown formats use `.img`.

## Lazy reads and write protection

Photo bytes are fetched only when a client reads a file, using its remote media
ID and requested byte range. The virtual tree rejects create, write, truncate/
resize, overwrite, metadata mutation, delete, rename, and move operations.
Normal read opens retain Windows' auxiliary `READ_CONTROL` right.

TEST5's access-denied regression came from treating `READ_CONTROL`
(`0x00020000`) as a content mutation. The TEST6 mutation mask instead blocks
`WRITE_DAC` (`0x00040000`) and `WRITE_OWNER` (`0x00080000`) alongside actual
write/delete rights, while allowing read-only opens that include `READ_CONTROL`.

## TEST6 validation

The live validation read the first 1,024 bytes through `O:\Fotos`, copied one
photo to a local temporary file, verified the complete file with size and
SHA-256 against the direct API download, and opened the local copy in Windows
Photos. No write operation was performed against O2 during that validation.

The permanent photo-library tests use synthetic IDs, dates, sizes, and content,
plus nonfunctional `OFFLINE_FIXTURE_*` placeholders where authentication fields
are required by the client. They contain no captured account values or usable
credentials.
