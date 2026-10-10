# RecycleBinToolkit

Move files and directories to the Windows Recycle Bin from .NET, with per-item diagnostics that
tell you *why* an item failed — and read, empty, and inspect the Recycle Bin itself.

```csharp
using RecycleBinToolkit;

await FileRecycler.MoveToRecycleBinAsync(@"W:\Users\me\Downloads\archive.zip");

await RecycleBin.GetItemsAsync();
await RecycleBin.EmptyAsync(@"W:\");
```

- **Targets** `net10.0`, Windows only. The library is `AnyCPU`; the test project is `x64`.
- **Dependency**: `Microsoft.Windows.CsWin32` only, and it is a build-time source generator.
- **One path to the Recycle Bin**: the bin's own files, written and read with ordinary file calls.

---

## What it is

The Recycle Bin is a directory per volume — `<volume>$RECYCLE.BIN\<SID>\` — holding paired `$I`
records and `$R` payloads. This library writes and reads those directly, with per-item diagnostics
that tell you *why* an item failed:

| Problem with a naive implementation | What this library does |
| --- | --- |
| Per-item failures are invisible | Every item is moved on its own and reported on its own |
| One locked file silently skips the rest | Only genuinely failed items are retried; the rest succeed on the first attempt |
| A single bad path aborts the whole queue | Path problems are reported per item and never abort a batch |
| A path that cannot be described is deleted **permanently** while reporting success | Detected before the move happens; the policy is yours to choose |
| A delete that lands outside the Recycle Bin is reported as success | The destination is chosen by the library itself, so there is no reported location to disagree with |

## Why it exists

Because a delete that silently destroys the file while reporting success is the one outcome a
Recycle Bin library exists to prevent. Every refusal in this library — a path that cannot be
described, a volume that cannot recycle — happens before anything is attempted, with the file left
where the caller left it, and every removal for good happens only where the caller explicitly asked
for it and is named as such.

---

## Requirements

| | |
| --- | --- |
| OS | Windows 10 version 1607 (build 14393) or later — the first build that reads the version 2 index records this library writes |
| Runtime | .NET 10 |
| Architecture | Any |
| Permissions | Write access to the file, and to its parent directory |

The API is annotated `[SupportedOSPlatform("windows6.0.6000")]`, so calling it from a
cross-platform target produces an analyzer warning unless you guard with
`OperatingSystem.IsWindows()`.

There is no stable version yet and no compatibility promise, so the public surface may still change
between builds. Two things in it are worth knowing are not accidental: `RecycleBinContents.TotalSize` is
derived when the listing is constructed rather than settable afterwards, and `RecycleBinEmptyResult.Elapsed`
is the time the caller waited, including queueing behind other work. Both were chosen so a result cannot
disagree with the measurements it came from.

## Installation

```bash
dotnet add package RecycleBinToolkit
```

Or reference the project directly:

```xml
<ProjectReference Include="path/to/RecycleBinToolkit/RecycleBinToolkit.csproj" />
```

---

## Quick start

One file:

```csharp
using RecycleBinToolkit;

await FileRecycler.MoveToRecycleBinAsync(@"W:\Users\me\Downloads\archive.zip");
```

A batch, reporting failures as data:

```csharp
// TopDirectoryOnly on purpose: a whole tree is one item, and the batch overload is for many
// paths the caller already has.
var paths = Directory.GetFiles(@"W:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

// This overload never throws for a per-item failure — a bad path lands in result.Failures
// and the rest of the batch still runs.
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

if (!result.Succeeded)
{
    foreach (var failure in result.Failures)
    {
        Console.Error.WriteLine(
            $"{failure.Path}: {failure.Stage} (0x{failure.HResult:X8}) after {failure.Attempts} attempt(s)");
    }
}
```

Never throwing, for a background service:

```csharp
IReadOnlyList<string?> paths = [];
var options = new RecycleBinOptions { ThrowOnError = false };

// An empty list is a valid call: it succeeds and does nothing. Entries may also be null or
// missing, each reported as a failure without aborting the batch.
var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

if (!result.Succeeded)
{
    foreach (var failure in result.Failures)
    {
        Console.Error.WriteLine($"recycle failed for {failure.Path}: {failure.Stage} 0x{failure.HResult:X8}");
    }
}
```

---

## API

### Methods

| Method | Returns | Description |
| --- | --- | --- |
| `FileRecycler.MoveToRecycleBinAsync(string? filePath, RecycleBinOptions? options = null, CancellationToken cancellationToken = default)` | `Task<bool>` | Recycles one file or directory. `true` when it is gone from disk. |
| `FileRecycler.MoveToRecycleBinAsync(IReadOnlyList<string?> filePaths, RecycleBinOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinResult>` | Recycles a batch and reports the outcome of every item. |
| `RecycleBin.GetItemsAsync(string? volumeRoot = null, RecycleBinOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinContents>` | Lists what is in the Recycle Bin. |
| `RecycleBin.EmptyAsync(string? volumeRoot = null, RecycleBinOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinEmptyResult>` | Empties it, and reports how much it freed. |
| `RecycleBin.GetStatusAsync(string volumeRoot, RecycleBinOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinStatus>` | Whether it is in use for a volume, and why not if it is not. |

All five are safe to call from any thread.

### `MoveToRecycleBinAsync(string?, RecycleBinOptions?, CancellationToken)`

Recycles a single file or directory.

| Parameter | Type | Description |
| --- | --- | --- |
| `filePath` | `string?` | Path of the file or directory. |
| `options` | `RecycleBinOptions?` | Retry, verification and error-handling settings. `null` means `RecycleBinOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Cancels the scan that validates the path and the work itself. |

**Returns** `true` when the path is gone from disk, whether it was recycled or removed permanently
under an explicit policy.

**Exceptions**

| Exception | When |
| --- | --- |
| `ArgumentException` | `filePath` is null, empty or whitespace and `ThrowOnError` is on |
| `FileNotFoundException` | `filePath` does not exist and `ThrowOnError` is on |
| `IOException` | The move failed, or the volume cannot recycle the path and `NoRecycleBinAction` is `Throw`, or that action is `DeletePermanently` and the file system refused the removal — and `ThrowOnError` is on. The file is left in place in every one of those cases. `HResult` carries the file system's own code, or `0` where the volume was refused from configuration |
| `NotSupportedException` | The path names its volume by GUID and `ThrowOnError` is on. Nothing was attempted; address the same location by drive letter instead |
| `PlatformNotSupportedException` | This machine is older than Windows 10 build 14393, so it could not read the records this library writes. Nothing was attempted, no file was touched, and `ThrowOnError` does **not** suppress it |
| `OperationCanceledException` | `cancellationToken` was cancelled before the work started |

With `ThrowOnError` off, nothing is thrown: a bad path returns `false`.

> **Overload note.** A bare `MoveToRecycleBinAsync(null)` is ambiguous between the two overloads and
> does not compile. Cast it: `(string?)null`, or pass a list.

### `MoveToRecycleBinAsync(IReadOnlyList<string?>, RecycleBinOptions?, CancellationToken)`

Recycles a batch. This overload **never throws** for a per-item failure.

| Parameter | Type | Description |
| --- | --- | --- |
| `filePaths` | `IReadOnlyList<string?>` | Paths to recycle. Entries may be null or missing; each is reported as a failure without aborting the batch. Repeated paths are recycled once. |
| `options` | `RecycleBinOptions?` | As above. |
| `cancellationToken` | `CancellationToken` | Stops the wait between attempts. |

**Returns** a `RecycleBinResult` describing every item.

**Exceptions** `ArgumentNullException` when `filePaths` is null, `OperationCanceledException` when the
token is cancelled. Per-item problems are data, not exceptions.

---

## The Recycle Bin itself

`RecycleBin` is a second entry point next to `FileRecycler`. Where `FileRecycler` moves items *in*,
`RecycleBin` reads, empties, and reports on the bin as a whole.

All three of its methods are batch-shaped: they never throw for an environmental problem. A volume
that is not mounted or an entry that cannot be described is data in the result, because a service
that lists or empties drives must survive one unmounted letter.

### `GetItemsAsync`

Lists the Recycle Bin.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to read, such as `"W:\"`, or `null` for every volume. A path inside a volume is not a volume root: it is reported in `Failures` rather than answered for the whole volume containing it. |
| `options` | `RecycleBinOptions?` | `null` means `RecycleBinOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Cancels the wait. |

**Returns** a `RecycleBinContents`.

Each volume's bin is enumerated directly, so `volumeRoot` selects which directories to read. **Every volume is
read at the same time**, each on its own thread: a Recycle Bin is one directory per volume, so two volumes are
two directories on two devices and nothing about them contends — one disk waiting on its head does not hold up
another. The answer's order is the order the volumes were asked about and does not depend on which one finished
first.

A path inside a volume names no volume of its own, and it is refused rather than widened
to the volume containing it — as `EmptyAsync` refuses it and `GetStatusAsync` answers it with
`VolumeNotFound`. Refused rather than answered empty, because an empty listing is a claim about the bin
and a caller whose root was one character off cannot tell that from a bin that really is empty.

### `EmptyAsync`

Empties the Recycle Bin.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to empty, or `null` for every volume, in which case every mounted Fixed or Removable volume is covered in a single call. A path inside a volume is not a volume root: it is reported in `Failures` rather than emptied as that whole volume. |
| `options` | `RecycleBinOptions?` | `null` means `RecycleBinOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Cancels the sweep. The sweep stops between entries and raises, so a caller who cancels gets no result at all, and what was removed is found by reading the bin rather than by reading the result. |

**Returns** a `RecycleBinEmptyResult`.

The counts are exact rather than measured: every entry's own record carries its size, so the
totals are summed from the records being deleted. That also means a record whose payload is already gone
is removed and counted. Neither figure is ever negative.

### `GetStatusAsync`

Whether the Recycle Bin is in use for one volume.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string` | Volume root to inspect. Must be a volume root, not a directory inside one. |
| `options` | `RecycleBinOptions?` | Accepted for uniformity and **not used**. This method answers what a volume's configuration says, not how items are moved. |
| `cancellationToken` | `CancellationToken` | Checked before the work starts. |

**Returns** a `RecycleBinStatus`. **Throws** `ArgumentException` for a null or blank path.

`GetStatusAsync` is the authoritative query on this, and `FileRecycler` asks it before every batch —
once per distinct volume root rather than once per path, so a batch of twenty thousand files on one
volume pays for one query. What it settles has three outcomes, not one: under the default
`NoRecycleBinAction.Throw` the path is refused there with the file left in place and nothing attempted,
under `DeletePermanently` the library removes it for good itself, and under `AllowIndeterminate`
it is recycled anyway. It reports *which* reason applies,
because what happens on the next delete is decided by a group policy, by a per-volume setting the user
owns, and by the kind of volume, so an answer that is only "off" throws away the one thing a caller can
act on.

---

## Types

### `FileRecycler`

The entry point. A static class with the two methods above. Work runs on the thread pool, so a caller
never blocks inside the bin.

### `RecycleBinOptions`

Immutable; set with object initialisers. Every property has a production default, so
`RecycleBinOptions.Default` is always a valid argument.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `RecycleBinOptions` | — | Shared instance carrying the defaults. |
| `MaxAttempts` | `int` | `6` | Attempts per item before it is reported as failed. Clamped to at least 1. |
| `InitialRetryDelay` | `TimeSpan` | `50ms` | Delay before the first retry; it doubles each attempt, with up to 25 % jitter. Negative values clamp to zero. |
| `OverallTimeout` | `TimeSpan` | `30s` | Upper bound on wall-clock time spent retrying the batch. A backoff that does not fit the remaining budget ends the loop immediately rather than starting an over-budget round. |
| `ThrowOnError` | `bool` | `true` | Whether a failure raises instead of being returned. |
| `NoRecycleBinAction` | `NoRecycleBinAction` | `Throw` | What happens to a file whose volume has no Recycle Bin to put it in. |
| `PreloadItemDetails` | `bool` | `false` | Read every `$I` record during `GetItemsAsync` instead of leaving each entry to read its own on first access. Off by default, because a listing reads no records at all that way. See [Lazy fields](#lazy-fields). |

```csharp
// Every property has a production default, so RecycleBinOptions.Default — which is what a null
// options argument means — is always a valid argument.
var options = new RecycleBinOptions
{
    MaxAttempts = 10,
    InitialRetryDelay = TimeSpan.FromMilliseconds(100),
    OverallTimeout = TimeSpan.FromSeconds(5),
    ThrowOnError = false,
    NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
};

// ThrowOnError is the global switch: with it off a refusal arrives in Failures instead of raising.
Console.WriteLine(options.OverallTimeout);
```

### `RecycleBinResult`

The outcome of a batch. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Succeeded` | `bool` | `true` when every requested path is gone. |
| `RecycledPaths` | `IReadOnlyList<string>` | Paths, as you supplied them, confirmed recycled. |
| `PermanentlyDeletedPaths` | `IReadOnlyList<string>` | Paths removed permanently instead of recycled, because you opted in. Empty unless that happened. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Paths that were not handled. Empty when `Succeeded`. |
| `Elapsed` | `TimeSpan` | Wall-clock time the caller waited. |
| `FirstFailure` | `RecycleFailure?` | The first recorded failure, or `null`. |
| `Create(...)` | `static RecycleBinResult` | Constructs a result. Useful in tests, rarely in application code. |

> `Succeeded` means **gone from disk**, not necessarily recycled. A file removed permanently under an
> explicit policy counts as success. Check `PermanentlyDeletedPaths` when that distinction matters.

```csharp
// Create exists so a test can hold a result without touching the file system. In application
// code the result comes back from a call.
var result = RecycleBinResult.Create(["a"], [], [], TimeSpan.Zero);

// Succeeded means gone from disk, not necessarily recycled: a file removed permanently under an
// explicit policy counts as a success. Check PermanentlyDeletedPaths when that matters.
Console.WriteLine($"{result.Succeeded} {result.Elapsed} {result.FirstFailure is null}");
```

### `RecycleFailure`

One item that was not handled.

| Member | Type | Description |
| --- | --- | --- |
| `Path` | `string` | The path exactly as you supplied it. |
| `HResult` | `int` | The file system's error code, or `0` when nothing was attempted — which includes a volume refused from its own configuration. |
| `Attempts` | `int` | Rounds the path went through. `0` means nothing was attempted — never validated, its volume cannot recycle it, it names its volume by GUID, or it was removed outright on your instruction. |
| `Stage` | `RecycleFailureStage` | Where in the pipeline it failed. |

**Reading `HResult`.** Success and failure are told apart by the severity bit, not by comparing with
zero.

```csharp
var failure = new RecycleFailure(@"W:\x.bin", unchecked((int)0x80270027), 1, RecycleFailureStage.Performing);

// Success and failure are told apart by the severity bit rather than by comparing with zero.
bool moveSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
Console.WriteLine(moveSucceeded);
```

A locked file reports `0x80270027` at stage `Performing`.

### `RecycleFailureStage`

| Value | Meaning |
| --- | --- |
| `PathValidation` | The path was empty, malformed, or pointed at nothing. |
| `Performing` | The move to the Recycle Bin failed for this item. |
| `NoRecycleBinOnVolume` | The volume holding the path cannot recycle it and the caller asked for a Recycle Bin, so the path was refused before anything was attempted and the file is where the caller left it. `Attempts` is `0`, and `HResult` is `0`: the refusal is settled from configuration. |
| `PermanentDeleteFailed` | You asked for a path to be removed for good — because its volume cannot recycle it and `NoRecycleBinAction` was `NoRecycleBinAction.DeletePermanently` — but the file system refused: a handle is held, the attributes are read-only. The file is exactly as you left it. |
| `Enumeration` | An entry in the Recycle Bin could not be read or removed, or a volume could not be read. Only reachable while reading or emptying the Recycle Bin. |
| `VolumeGuidPathNotAddressable` | The path names its volume by GUID, as `\\?\Volume{GUID}\…`. The volume has no drive letter to be measured, emptied or reported against, so the path was refused before anything was asked: `Attempts` is `0` and `HResult` is `0`. Address the same location by drive letter or mount point instead. |

`HResult` carries the file system's code where the file system refused, or `0` where nothing was
attempted. Test `(HResult & 0x80000000) != 0` to tell a refusal from a success code.

### `NoRecycleBinAction`

What to do with a path on a volume that cannot recycle it.

| Value | Effect |
| --- | --- |
| `Throw` | Default. The path is refused, the file stays where it is, and nothing is attempted. Raises only when `ThrowOnError` is also on. |
| `AllowIndeterminate` | The path is recycled even though the volume's answer was not `Enabled` — the answer was `Indeterminate` or `NoRecycleBinOnVolume`. The per-user directory under `$RECYCLE.BIN` is created and the file lands in it, so the volume gains a Recycle Bin it did not have. |
| `DeletePermanently` | The file is removed for good by the library itself and listed in `PermanentlyDeletedPaths`, never in `RecycledPaths`. A file system that refuses reports `PermanentDeleteFailed` and leaves the file in place. |

The values run from safest to most destructive, and `ThrowOnError` is still the global switch: with it
off, a refusal arrives in `Failures` instead of an exception.

Each batch is grouped by volume root and every distinct root is inspected once, so a batch of twenty
thousand files on one volume pays for one check rather than twenty thousand. Nothing is remembered
between calls: a volume mounted, or a policy applied, after one operation is seen by the next.

### `RecycleBinItem`

One entry in the Recycle Bin. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `OriginalPath` | `string` | Full path the entry was deleted from. Read from the entry's record on first access — see [Lazy fields](#lazy-fields). |
| `DeletedAt` | `DateTimeOffset` | When it was deleted, in the local offset. Read from the same record, on the same first access. |
| `Size` | `long` | Size in bytes. `0` for a directory — see [Limitations](#limitations). |
| `IsDirectory` | `bool` | Whether the entry is a directory. |

There is no `Name`: it is the last component of `OriginalPath`.

**There is no `RecycledPath`, so a listed entry cannot be restored from this listing.** To put an
entry back, walk the bin yourself — `<volume>$RECYCLE.BIN\<SID>\`, where each entry is a `$I`
record and a `$R` payload — as Explorer's own Restore does.

#### Lazy fields

`Size` and `IsDirectory` come from the bin directory itself, and the other two fields live in the
entry's `$I` record. A listing of eighteen thousand entries is eighteen thousand record reads —
about five seconds warm and thirty cold on a mechanical volume — so a listing reads **no records at
all** and each entry reads its own on the first access to either `OriginalPath` or `DeletedAt`. The
reading answers both, once, whichever was asked for first.

That means accessing these fields is I/O, and it can fail:

- An entry whose record was collected under it — an empty running against a caller holding a listing — raises
  `IOException`. The exception is cached, so a second access raises the same one rather than retrying.
- Set [`PreloadItemDetails`](#recyclebinoptions) to read every record during the listing instead. An entry
  whose record cannot be read is then reported in `RecycleBinContents.Failures` and left out of that volume's
  group in `Items`, rather than being returned and raising later.

#### Where each field comes from

| Field | Source |
| --- | --- |
| `OriginalPath`, `DeletedAt` | The entry's own `$I` record, read on first access |
| `Size`, `IsDirectory` | The `$R` payload's own directory entry, read during the listing |

#### What a listing does not show

An entry appears only when both halves of it are there: its `$R` payload and its `$I`
record. An entry whose payload is gone has no file to report a size for, and an entry whose record is gone
has no path to answer with — so neither is listed, and neither is a failure. Both are states a Recycle Bin is
briefly in, and the next `EmptyAsync` collects what is left of them.

For the same reason the listing does not filter by original path when you name a volume: it walks
`<volume>$RECYCLE.BIN\<SID>\` of exactly the volumes you asked for, so an entry it returns was deleted
from one of them. Matching on `OriginalPath` instead would read every record the listing had just declined
to read.

**Match this field against your own paths without regard to case.** `OriginalPath` is the path as it
was recorded, and the drive letter is written in whichever case Windows likes: an entry deleted from a
volume the caller reached for as `e:\` comes back as `E:\…`. The library does not rewrite it. Windows
paths are case-insensitive, so a caller comparing this field with a path it holds compares it that
way — which is what makes `--scratch 'e:\'` and `--scratch 'E:\'` behave identically, and an ordinal
comparison of the same pair reports a correct listing as empty.

> One original path can appear more than once. Deleting the same file twice leaves two entries with two
> deletion times, and both are listed.

### `RecycleBinContents`

The outcome of a listing. Its properties never change; `LoadItemDetails` fills in the items themselves.

| Member | Type | Description |
| --- | --- | --- |
| `Items` | `IReadOnlyList<RecycleBinVolumeContents>` | One group per volume that was read, in the order the volumes were asked about. A volume whose Recycle Bin is empty is one of the groups, holding nothing. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Entries that could not be described, at stage `Enumeration`. |
| `Elapsed` | `TimeSpan` | Wall-clock time spent enumerating. |
| `TotalSize` | `long` | Sum of `Size` over every group's entries, derived when the listing was built. |
| `LoadItemDetails(CancellationToken)` | `void` | Reads every item's lazy fields now, so no later access touches the disk. See [Loading every item's details](#loading-every-items-details). |

#### `RecycleBinVolumeContents`

What one volume's Recycle Bin holds.

| Member | Type | Description |
| --- | --- | --- |
| `VolumeRoot` | `string` | Volume root the entries were read from, such as `"E:\\"`. |
| `Items` | `IReadOnlyList<RecycleBinItem>` | The entries that volume's bin holds, in the order its directory gave them. Empty for a volume whose bin is empty. |

The grouping is what keeps a caller able to tell which disk an entry came from, which matters because the
volumes are read at the same time rather than one after another. The order is the order the volumes were asked
about and does not depend on which volume's walk finished first.

#### Loading every item's details

A listing reads no records at all, and each entry reads its own on first use. That is the right shape when you
want a few of them and it is the wrong one when you want all of them: the reads then happen one at a time, from
inside a loop that also has to do something else.

`LoadItemDetails` moves that cost into one batch:

- Items whose details are already in memory are skipped — every item of a listing taken with
  `PreloadItemDetails`. A listing with nothing pending does one pass over its items and no
  file reads. An item whose record has already failed to read is not "already read": it is tried again, because
  the record may be back.
- Volumes are read one after another, each on as many threads as its own hardware answers for: one per core
  on a solid-state disk, two per core on a mechanical one, each capped at eight, and one for a share. The same
  measure governs `PreloadItemDetails` and the empty, because it is the same answer to the same question about
  the same disk. A mechanical volume is not held to one thread: that rule came from writing a bin, where every
  record lands in one directory, and reading thirty thousand records is thirty thousand independent opens of
  files nobody is writing to. Measured cold, which is what a first read does, it is the difference between
  **50.7 s and 7.9 s** over 30,010 records on a mechanical volume, and **1.65 s and 0.49 s** warm.
  `LoadItemDetails` is the one operation that still reads the volumes one at a time, and that is deliberate:
  it has thirty thousand independent reads *per volume* and a degree of parallelism that has to answer to the
  drive, so multiplying it across volumes would be answering a question no drive was asked. The listing itself
  makes the opposite call — one directory walk per volume, whatever the volume is — and reads them together.
- An item whose record cannot be read is **left cold**, and still raises `IOException` when a caller asks for
  its details. Nothing here throws for one, and `Failures` does not change — it describes what the listing could
  not do.
- The items themselves are the same objects the listing returned, so `TotalSize` and any reference you already
  hold stay valid.
- **It blocks the calling thread.** Every other entry point here hands its work to a pool thread; this one runs
  the batch inline, because there is nothing to await. Do not call it on a UI thread — on a bin of eighteen
  thousand records on a cold mechanical disk it is tens of seconds of frozen UI.

Raising `OperationCanceledException` is the one thing it does throw, when the token is cancelled part way; items
read before that point stay read.

### `RecycleBinEmptyResult`

The outcome of an empty. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `VolumesEmptied` | `IReadOnlyList<string>` | Volume roots covered, each with a trailing separator. |
| `ItemsRemoved` | `long` | Entries that left the Recycle Bin. Measured, never negative. |
| `BytesFreed` | `long` | Bytes they occupied. Measured, never negative. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Volumes that could not be emptied or measured. |
| `Elapsed` | `TimeSpan` | Wall-clock time the caller waited. |

### `RecycleBinStatus`

The outcome of a status check. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `VolumeRoot` | `string` | The volume the answer is about. |
| `DriveType` | `DriveType` | What kind of volume it is; `Unknown` when that could not be read. |
| `Reason` | `RecycleBinAvailabilityReason` | Why it is or is not in use. |
| `IsEnabled` | `bool` | `true` only when `Reason` is `Enabled`. |

### `RecycleBinAvailabilityReason`

| Value | Meaning |
| --- | --- |
| `Enabled` | Nothing disables the Recycle Bin for this volume. |
| `DisabledByPolicy` | A group policy forbids it. Neither the user nor the application can change this, which is why it is named first whenever it applies. |
| `DisabledByUserPreference` | This volume is set to delete immediately instead of recycling. The user can change it. |
| `UnsupportedVolumeType` | The volume type has no Recycle Bin at all. |
| `VolumeNotFound` | The volume is not mounted, or the path given is not a volume root. |
| `NoRecycleBinOnVolume` | The volume exists and its type supports a Recycle Bin, but no bin was ever recorded for it. Nothing produces this reason anymore — an unconfigured volume answers `Indeterminate` — and the member stays because the numbers are contract. |
| `Indeterminate` | The volume exists but the answer could not be established. Deliberately not `Enabled`. |

`Enabled` is the only value that asserts a fact, and this library does not assert what it could not
read. The per-volume setting is keyed on the volume's GUID, and Windows writes that key only for
volumes it has configured a bin for, so an absent key means "this volume was never configured", not
"this volume recycles". Only a volume whose own key reads `NukeOnDelete=0` is answered `Enabled` from
configuration; anything else answers `Indeterminate`.

---

## Examples

Every example below is compiled as the body of a method in
`tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs`, so what is shown here is what was built
against. They are in the order they appear in that file.

### Recycle one file

```csharp
using RecycleBinToolkit;

// The library writes the Recycle Bin's own files under <volume>$RECYCLE.BIN\<SID>\ and reads them back
// with ordinary file calls. It needs Windows 10 version 1607 (build 14393) or later, because that is the
// first build that reads the index records it writes; below that the call raises
// PlatformNotSupportedException, and ThrowOnError does not suppress that.
await FileRecycler.MoveToRecycleBinAsync(@"W:\Users\me\Downloads\archive.zip");

var contents = await RecycleBin.GetItemsAsync(@"W:\");
await RecycleBin.EmptyAsync(@"W:\");
```

### List what is in the Recycle Bin

```csharp
var contents = await RecycleBin.GetItemsAsync();

// One group per volume, in the order the volumes were asked about. A volume whose Recycle Bin is
// empty is one of the groups and holds nothing, so an empty bin and a volume that was never
// reached do not look alike.
foreach (var volume in contents.Items)
{
    Console.WriteLine($"{volume.VolumeRoot} — {volume.Items.Count} item(s)");

    foreach (var item in volume.Items)
    {
        // Size is 0 for a directory — its contents cannot be enumerated without elevation, so
        // IsDirectory is the honest signal that no figure is available.
        //
        // OriginalPath and DeletedAt are read from the entry's record on first access, which is I/O
        // and can raise IOException if the record was collected meanwhile. The two come out of one
        // reading, so asking for either gives you both.
        Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
    }
}

// TotalSize is derived from every group's items when the listing is built, so it cannot disagree
// with them.
Console.WriteLine($"{contents.TotalSize} byte(s) in total");
```

### List the Recycle Bin and read every record up front

```csharp
// A listing reads no $I records by default, which is what makes one walk of the bin directory
// enough. A caller that is going to read every entry's path anyway can have the records read during
// the listing instead of one at a time from its own loop — and an entry whose record cannot be read
// is reported in Failures rather than raising later.
var eager = new RecycleBinOptions { PreloadItemDetails = true };

var contents = await RecycleBin.GetItemsAsync(@"W:\", eager);

foreach (var volume in contents.Items)
{
    foreach (var item in volume.Items)
    {
        Console.WriteLine($"{item.OriginalPath} — {item.Size} byte(s)");
    }
}

foreach (var failure in contents.Failures)
{
    Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
}
```

### Read every item's details before looping over them

```csharp
var contents = await RecycleBin.GetItemsAsync();

// Read every item's OriginalPath and DeletedAt up front, in parallel, so the loop below never blocks on I/O.
contents.LoadItemDetails();

foreach (var volume in contents.Items)
{
    foreach (var item in volume.Items)
    {
        Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
    }
}
```

### List one volume, and hear about anything unreadable

```csharp
// Each volume's bin is enumerated directly, so this reads exactly the volume named.
var onW = await RecycleBin.GetItemsAsync(@"W:\");

foreach (var failure in onW.Failures)
{
    Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
}
```

### Empty the Recycle Bin and report what it freed

```csharp
var emptied = await RecycleBin.EmptyAsync(@"W:\");

// Both figures are summed from the records being deleted. Neither is ever negative.
Console.WriteLine($"freed {emptied.BytesFreed} byte(s) from {emptied.ItemsRemoved} item(s)");

foreach (var failure in emptied.Failures)
{
    Console.Error.WriteLine($"{failure.Path} was not emptied: 0x{failure.HResult:X8}");
}
```

### Act on a volume whose Recycle Bin is switched off

```csharp
// A volume root, not a directory inside one.
var status = await RecycleBin.GetStatusAsync(@"W:\");

if (!status.IsEnabled)
{
    Console.Error.WriteLine($"the Recycle Bin is not in use on {status.VolumeRoot}: {status.Reason}");

    // The reasons differ in who can fix them, which is why this is not a bool. Indeterminate
    // is deliberately not Enabled: the library does not assert what it could not read.
    if (status.Reason is RecycleBinAvailabilityReason.DisabledByPolicy)
    {
        Console.Error.WriteLine("an administrator set this; nothing in the application can change it");
    }
}
```

### Recycle a directory tree

```csharp
// A directory tree is one path. The single-path overload returns a bool and no reason, so the
// batch overload with one element is the way to learn why it failed.
IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

if (result.Succeeded)
{
    Console.WriteLine("recycled");
}
else
{
    foreach (var failure in result.Failures)
    {
        Console.Error.WriteLine($"{failure.Path} — {failure.Stage}, 0x{failure.HResult:X8}");
    }
}
```

### Report every failure with its reason

```csharp
// A path that is not there comes back as a failure and does not stop the other entries.
IReadOnlyList<string?> paths = [@"W:\Projects\build-output", @"W:\Projects\gone.log"];
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

foreach (var failure in result.Failures)
{
    Console.Error.WriteLine(
        $"{failure.Path} — {failure.Stage}, hresult=0x{failure.HResult:X8}, attempts={failure.Attempts}");
}
```

### Get long paths into the Recycle Bin

```csharp
// A path hundreds of characters long recycles with no staging, no policy and no limit: the
// destination inside the bin is short whatever the source was.
IReadOnlyList<string?> paths = [@"W:\Projects\a-very-long-path\build-output"];
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

Console.WriteLine($"{result.RecycledPaths.Count} path(s) recycled");
```

### Never let a file be destroyed without a decision

```csharp
IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];
var options = new RecycleBinOptions
{
    NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
};

var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

// Only paths on volumes that cannot recycle are affected; everything else still goes to the bin.
if (result.PermanentlyDeletedPaths.Count > 0)
{
    Console.Error.WriteLine($"{result.PermanentlyDeletedPaths.Count} file(s) were deleted, not recycled.");
}
```

### Cancel the wait

```csharp
IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

// Cancelling stops the batch between paths: a cancelled call stops part way and raises without
// returning a result, so which files went is found by reading the bin.
await FileRecycler.MoveToRecycleBinAsync(paths, cancellationToken: cts.Token);
```

### Refuse a batch on a volume without a Recycle Bin

```csharp
IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];
var options = new RecycleBinOptions { ThrowOnError = false };

// The default NoRecycleBinAction is Throw: on a volume with no Recycle Bin the path is refused
// before anything is attempted. With ThrowOnError off the refusal comes back as data instead of
// an exception.
var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

foreach (var failure in result.Failures)
{
    Console.Error.WriteLine($"{failure.Path} was not recycled: {failure.Stage} 0x{failure.HResult:X8}");
}
```

### Assert in a test

`Check.*` here is the test project's own assertion helper, and it is what the block below reads in
`ReadmeExamples.cs` too — the two are character for character. In a project with xunit the same three
lines read `Assert.True`, `Assert.Equal` and `Assert.False`.

```csharp
IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

Check.True(result.Succeeded, "the batch succeeded");
Check.Equal(0, result.Failures.Count, "no failures");

// Succeeded is derived from the library's own checks; File.Exists is the independent one.
foreach (var path in result.RecycledPaths)
{
    Check.False(File.Exists(path), $"{path} is gone");
}
```

---

## Throughput, and why reading and writing are not alike

Recycling a batch of 3,000 files of 64 KiB runs at about 616 files/s on a mechanical volume and 625
on a solid-state one — the cost is filesystem round trips, not bandwidth. Four-kilobyte files recycled
at 583 and 662, within the run-to-run spread, so the file size is not part of the cost either. Every
figure is the median of three runs.

Reading scales, on either kind of disk: it changes nothing about the directory it reads. Reading 30,010
records cold on a mechanical volume took 73.1 s on one thread and 7.7 s on eight; the same sweep on a
solid-state volume over 36,009 records took 92.0 s and 16.1 s. Warm, the same two volumes took 1550 ms and
351 ms against 1892 ms and 457 ms. Writing does not scale, and the reason is a directory — a Recycle Bin
has exactly one per volume, and every record written and every payload moved lands in it. The same
recycling measured 542 files/s on one thread and 189 on eight, on a solid-state volume, so paths move one
at a time on purpose. Reading a bin in parallel is where the concurrency is; putting files into one is
not. What makes a recycle fast is the number of round trips per file — three: one `FileInfo` query in
validation, one create-and-write for the record, one move — not the number of threads, which is why that
is what the code optimises.

A warm read costs about 47 microseconds per record, and that figure is the same on a solid-state volume
and on a mechanical one — the cache has the bytes, so what is being measured is what the filesystem
charges per file rather than bandwidth. Turning the bytes into a path and a timestamp costs about a
tenth of a microsecond, two parts in a thousand of it. That is why reading is one loop rather than a
pipeline: there is nothing to overlap.

**The version requirement is a refusal, not a warning.** Windows 10 build 14393 is the first build
that reads a version 2 index record. An older build does not reject one — it reads it as something it
cannot use, so the item appears in the bin and cannot be restored from it. `FileRecycler` therefore
throws `RecycleBinNativeBackendUnavailableException` (a `PlatformNotSupportedException`) before it
touches anything, and `ThrowOnError` does not suppress it: there is no path to report and no
`RecycleFailureStage` that describes it, because nothing failed. `GetItemsAsync` and `EmptyAsync` are
not gated — reading version 1 records is supported as well as writing version 2, and emptying parses
nothing — so a machine too old to *write* can still *read* and *clear* at full speed.

---

## How it works

```
FileRecycler          validates, de-duplicates, recycles on the pool
   ├─ NativeBinLayout         $RECYCLE.BIN\<SID>\ and the $I/$R name pair
   ├─ NativeIndexFile         version 1 and 2 records, read and written
   ├─ NativeBinEntries        one walk of the bin, pairing each $I record with its $R payload
   ├─ NativeRecycleRunner     record first, then the move; retries are per path
   ├─ NativeBinLister         pairs from the walk, every volume at once; records only under PreloadItemDetails
   └─ NativeBinEmptier        payload first, then the record, counted from the walk

RecycleBinContents.LoadItemDetails  warms a listing after the fact
  └─ ItemDetailLoader        one volume at a time, at that volume's own degree of parallelism

RecycleBin            resolves volumes, reports
  ├─ RecycleBinVolumeSet   which volumes an operation covers
  ├─ RecycleBinVolumeState  volume table + registry facts
  └─ RecycleBinVolumePolicy the decision, separated so it can be checked exhaustively
```

**Record first, then the move.** A crash between the two leaves the caller's file exactly where it
was and an index with no payload — litter the next empty collects. The other order leaves data that
is gone and recorded nowhere, which is the one outcome a Recycle Bin exists to prevent.

**Retries are per path, not per batch.** A move that fails is a property of one path, and retrying
the others would repeat work that already succeeded. Only the items that failed are retried.

**One thread to write, many to read.** Every record written and every payload moved lands in one
directory per volume, and a directory is the one thing a file system cannot take concurrent writers
on — so paths move one at a time on purpose. Reading changes nothing, so records are read in
parallel at each volume's own degree of parallelism: one thread per core on a solid-state volume, two
on a mechanical one, because each of those requests is a seek and a second one in flight covers the
mechanism's dead time. That is thirty thousand independent reads, not thirty thousand writers.

**The per-volume bin is the unit.** Each volume's `$RECYCLE.BIN\<SID>\` is walked directly, so naming
a volume selects which directories to read and to empty — never a filter over an enumeration that
already happened.

**`GetStatusAsync` answers configuration, but not the future.** Where the registry records a volume's
own setting, that setting is the whole answer. A volume nothing was recorded for answers
`Indeterminate`: unproven, never enabled. That is what makes it trustworthy enough for
`FileRecycler` to refuse a path rather than destroy it. It still does not predict the next delete:
a volume mounted after the call, or a policy applied after it, changes the answer without the call
having been wrong. Nothing is cached, so each call asks the question again.

---

## Limitations

Stated plainly, because these are properties of Windows rather than choices.

- **A path of any length recycles.** The destination inside the bin is short whatever the source was,
  so there is no length limit, no staging, and no policy for long paths.
- **Cancellation stops the batch, not the wait.** The token is checked between paths, so a cancelled
  batch stops part way and raises `OperationCanceledException` carrying no result: which files went is
  found by reading the bin.
- **Temporary locks are retried, not eliminated.** An indexer, preview handler or antivirus holding a
  file open is transient; the retry policy absorbs it. A lock held for the whole run fails the item,
  and only that item.
- **This library is Windows-only.** There is no cross-platform fallback.
- **A recycled directory reports `Size` as zero.** Its contents cannot be enumerated without
  elevation, so there is no figure to give. `IsDirectory` is the honest signal that a size is not
  available.
- **A listing covers exactly the volumes named.** Each volume's bin is walked directly, so a caller
  who wants only `W:\` pays only for `W:\` — and it cannot see a volume that is not mounted.
- **`GetStatusAsync` answers configuration, but not the future.** Where the registry records a
  volume's own setting, that setting is the whole answer. A volume nothing was recorded for answers
  `Indeterminate`, never `Enabled`: the library does not assert what it could not read. It still does
  not predict the next delete: a volume mounted after the call, or a policy applied after it, changes
  the answer without the call having been wrong. Nothing is cached, so each call asks the question
  again.
- **A per-volume key that outlives its bin still reads `Enabled`.** Windows records a volume's Recycle
  Bin setting when it configures one, and nothing removes that record when the bin later goes. A key
  left behind on a volume with no `$RECYCLE.BIN` is therefore treated as evidence that the volume
  recycles, because configuration is the evidence throughout.
- **The per-volume Recycle Bin capacity is not treated as a switch.** A capacity of `0` is not
  reported as disabled, because the Recycle Bin dialog offers no such setting and no measurement of its
  behaviour is recorded here.
- **Emptying a large Recycle Bin is slow, and that is file deletion.** Measured: emptying roughly
  13,600 accumulated entries took 216 s. There is no progress callback to report from and no way to
  make it incremental; a caller that needs responsiveness should empty one volume at a time.

---

## Testing

```bash
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --all
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --filter NativeBinSuite
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --load
```

`--scratch <root>` is required and names the volume to test on. It has to be a volume root — `D:\` or
`\\server\share\`, or `//server/share` where the command line cannot carry two leading backslashes. A
directory inside a volume is refused, and so is a bare drive letter, because both resolve against something
other than the volume they name. Without the flag nothing runs and the exit code is 2: the suite writes
files and removes directories, so the volume is yours to pick and not the runner's to guess.

Inside the root the run creates `D:\{guid}` and works in `scratch` and `long` beneath it, then removes the
whole tree when it finishes. Nothing is derived from the repository, so no test writes inside the working
copy, and no test acts on a path that did not come from `--scratch` — the volume comes from the flag and
from there down. A drive letter or a share written into a case is data for a path predicate: it decides
what a string means and touches nothing, so `NormalizeVolumeRoot(@"W:\")` and a share-root spelling are
both ordinary assertions rather than something a case acts on.

`--filter` matches any part of a case name, and every case is named `<Suite>.<Case>`. It exits
non-zero when a case fails.

`--scratch` accepts any volume. Some cases need one Windows records as recycling, which it writes under
`HKCU\...\Explorer\BitBucket\Volume\{GUID}\NukeOnDelete=0` when "Don't move files to the Recycle Bin" is
clear for that drive; a volume with no such key is answered `Indeterminate` — refused by the default
`NoRecycleBinAction.Throw`. The run asks the library once which kind of volume it was given, and each
case that needs the other kind skips rather than fails.

The suite is a console application with no external packages: 163 cases covering batch and single-path
recycling, per-item failure reporting, selective retry against a self-inflicted transient lock, long
paths with no limit, concurrent callers, directories, 10 MB files, volumes with no Recycle Bin, and a
ten-cycle stress run asserting 100 % Recycle Bin delivery. Listing, emptying and status checking are
covered by their own suites. The default run writes only inside the directory it claimed under
`--scratch`. On a volume with no Recycle Bin the cases that recycle there skip rather than fail, and
the cases that are about such a volume run instead of skipping.

**Of the 163 cases, `--all` runs 159.** Four are load cases that run only on request: the ten-cycle stress
run, and the three cases that each move the whole 94-file fixture set in one call.
`--all` and `--filter` never run them — `--load` runs those four cases and nothing else. Nothing is
covered by them alone; the claims sit in ordinary cases too — `WorkflowSuite.FiveTouchedFilesAreAllRecycled`
and `EmptySuite.EmptyReportsWhatItFreed` for the batch, `FacadeSuite.LargeFileIsRecycled` for the bytes.
The remainder is the volume's decision rather than a flag's: 149 pass and 10 skip where there is a bin,
and nothing fails.

**A share has to survive your command line.** `--scratch "\\server\share\"` is right in PowerShell and in
`cmd.exe`, which treat backslashes literally. It is wrong in anything that treats `\\` as an escape —
bash, or any tool that unescapes the command string before the shell ever sees it — because what arrives
is `\server\share\`, whose root is the *current drive*; a directory created from it lands wherever the
process happens to be. The runner refuses a root that is not a volume root rather than acting on whatever
drive is current, so the mistake goes red and costs nothing — but it goes red. Write the share as
`//server/share/` there. The forward-slash spelling names the same volume, no shell rewrites it, and the
runner reads it as the backslash root Windows writes.

**The Recycle Bin suites empty it.** `EmptySuite` removes what the earlier suites recycled, and it is
the only way to assert that an entry really left. Everything else only ever adds to it.

**One test keeps the deltas honest.** Every suite reads the Recycle Bin through `RecycleBinProbe`, which
walks the bin directory for counts and sizes. The walk shares the record reader with the library's own
listing, so what it proves is that the walk and the reader agree with the filesystem about counts and
sizes — not an independent view of the bin. Deltas are what the suites assert: the same walk before and
after one known delete.

The harness must cope with the Windows indexer: it creates and recycles thousands of files in one
directory, so handles appear and disappear underneath it. Cleanup retries for that reason.

Two things about the harness are worth knowing before reading its timings. Reading a count walks the
bin directory rather than reading records, because answering "did the count go up by one" does not need
the entries. What the suites assert about the listing itself lives in `ListerSuite`, which pays for the
reads deliberately.

`EmptySuite` empties the volume it runs against. It is the one suite that changes machine state rather
than only reading it, and on a Recycle Bin holding tens of thousands of entries it will take a long
time, because emptying that many files is file deletion.

## Contributing

Every code block in this file also exists as a compiling method in
`tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs`, and the two are edited together. A new example
goes into both in the same change; the rule is spelled out in `AGENTS.md` at the repository root.

The project has a specification at
`docs/superpowers/specs/2026-10-06-recycle-bin-reliability-design.md` that records the measurements
the design rests on. Behaviour claimed here should be traceable to a measurement there, and new
behaviour should arrive with a test that was seen to fail first.

## License

No license has been chosen yet. Choose one before publishing — until then the code is unlicensed and
not usable outside this repository.
