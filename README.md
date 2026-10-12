# RecycleBinToolkit

Move files and directories to the Windows Recycle Bin from .NET, with per-item diagnostics that
tell you *why* an item failed — and read, restore, empty, and inspect the Recycle Bin itself.

```csharp
using RecycleBinToolkit;

await RecycleBin.RecycleAsync(@"W:\Users\me\Downloads\archive.zip");

await RecycleBin.GetItemsAsync();
await RecycleBin.EmptyAsync(@"W:\");
```

- **Targets** `net10.0`, Windows only. The library is `AnyCPU`; the test project is `x64`.
- **Dependency**: `Microsoft.Windows.CsWin32` only, and it is a build-time source generator.
- **One path to the Recycle Bin**: the bin's own files, written and read with ordinary file calls.
  No shell, no COM, no `SHFileOperation`.

---

## What it is

A Recycle Bin library for .NET that reports per item, and never destroys a file while claiming it
did not:

| Problem with a naive implementation | What this library does |
| --- | --- |
| Per-item failures are invisible | Every item is moved on its own and reported on its own |
| One locked file silently skips the rest | Only genuinely failed items are retried; the rest succeed on the first attempt |
| A single bad path aborts the whole queue | Path problems are reported per item and never abort a batch |
| A path that cannot be described is deleted **permanently** while reporting success | Detected before the move happens; the policy is yours to choose |
| A delete that lands outside the Recycle Bin is reported as success | The destination is chosen by the library itself, so there is no reported location to disagree with |
| A delete that cannot be taken back, because nothing knows where the item came from | A restore puts the entry back where its own record says it was deleted from, and reports the outcome in [`RecycleBinRestoreResult`](#recyclebinrestoreresult) |

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

**The version requirement is a refusal, not a warning.** Build 14393 is the first that reads a
version 2 index record. An older build does not reject one — it reads it as something it cannot
use, so the item appears in the bin and cannot be restored from it. `RecycleAsync` therefore throws
`RecycleBinNativeBackendUnavailableException` (a `PlatformNotSupportedException`) before it touches
anything, and `ThrowOnError` does **not** suppress it: nothing failed, so there is nothing to
report as data. `GetItemsAsync`, `RestoreAsync` and `EmptyAsync` are not gated — reading version 1
records is supported as well as writing version 2, and emptying parses nothing — so a machine too
old to *write* can still *read* and *clear* at full speed.

There is no stable version yet and no compatibility promise, so the public surface may still change
between builds. Two members are worth knowing up front: `RecycleBinContents.TotalSize` is derived when
the listing is constructed rather than settable afterwards, and the `Elapsed` on every result is the
time the caller waited, including queueing behind other work.

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

await RecycleBin.RecycleAsync(@"W:\Users\me\Downloads\archive.zip");
```

A batch, reporting failures as data:

```csharp
// TopDirectoryOnly on purpose: a whole tree is one item, and the batch overload is for many
// paths the caller already has.
var paths = Directory.GetFiles(@"W:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

// This overload never throws for a per-item failure — a bad path lands in result.Failures
// and the rest of the batch still runs.
var result = await RecycleBin.RecycleAsync(paths);

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

// An empty list is a valid call: it succeeds and does nothing. Entries may also be null or
// missing, each reported as a failure without aborting the batch.
var result = await RecycleBin.RecycleAsync(paths);

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
| `RecycleBin.RecycleAsync(string? filePath, RecycleOptions? options = null, CancellationToken cancellationToken = default)` | `Task<bool>` | Recycles one file or directory. `true` when it is gone from disk. |
| `RecycleBin.RecycleAsync(IReadOnlyList<string?> filePaths, RecycleOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinResult>` | Recycles a batch and reports the outcome of every item. |
| `RecycleBin.GetItemsAsync(string? volumeRoot = null, GetItemsOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinContents>` | Lists what is in the Recycle Bin. |
| `RecycleBin.RestoreAsync(IReadOnlyList<RecycleBinItem> items, RestoreOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinRestoreResult>` | Puts listed entries back where their records say they came from. |
| `RecycleBin.RestoreAsync(IReadOnlyList<RecycleBinVolumeContents> volumes, RestoreOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinRestoreResult>` | The same, for the volume groups of a listing. |
| `RecycleBin.EmptyAsync(string? volumeRoot = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinEmptyResult>` | Empties it, and reports how much it freed. |
| `RecycleBin.GetStatusAsync(string volumeRoot, CancellationToken cancellationToken = default)` | `Task<RecycleBinStatus>` | Whether it is in use for a volume, and why not if it is not. |

All seven are safe to call from any thread.

### `RecycleAsync(string?, RecycleOptions?, CancellationToken)`

Recycles a single file or directory.

| Parameter | Type | Description |
| --- | --- | --- |
| `filePath` | `string?` | Path of the file or directory. |
| `options` | `RecycleOptions?` | Retry, verification and error-handling settings. `null` means `RecycleOptions.Default`. |
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

> **Overload note.** A bare `RecycleAsync(null)` is ambiguous between the two overloads and
> does not compile. Cast it: `(string?)null`, or pass a list.

### `RecycleAsync(IReadOnlyList<string?>, RecycleOptions?, CancellationToken)`

Recycles a batch. This overload **never throws** for a per-item failure.

| Parameter | Type | Description |
| --- | --- | --- |
| `filePaths` | `IReadOnlyList<string?>` | Paths to recycle. Entries may be null or missing; each is reported as a failure without aborting the batch. Repeated paths are recycled once. |
| `options` | `RecycleOptions?` | Retry settings, plus `NoRecycleBinAction` for a path on a volume that cannot recycle it — a refused path arrives in `Failures` at stage `NoRecycleBinOnVolume` rather than raising. `null` means `RecycleOptions.Default`. `ThrowOnError` is on this type but **not read here**: the batch reports every per-item failure rather than raising, so setting it changes nothing on this overload. Use `RecycleAsync(string?, …)` if you want a single failure to raise. |
| `cancellationToken` | `CancellationToken` | Cancels the scan that validates the batch and the work between paths. A cancelled batch stops part way and raises `OperationCanceledException` carrying no result: which files went is found by reading the bin. |

**Returns** a `RecycleBinResult` describing every item.

**Exceptions** `ArgumentNullException` when `filePaths` is null, `OperationCanceledException` when the
token is cancelled. Per-item problems are data, not exceptions.

---

## The Recycle Bin itself

`RecycleBin` is one class, and this is the half of it that looks at the bin from the outside. `RecycleAsync`
moves items *in*; the four methods below read, restore, empty, and report on what is already there.

All four are batch-shaped: they never throw for an environmental problem. A volume
that is not mounted or an entry that cannot be described is data in the result, because a service
that lists, restores, empties or checks drives must survive one unmounted letter.

### `GetItemsAsync`

Lists the Recycle Bin.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to read, such as `"W:\"`, or `null` for every volume. A path inside a volume is not a volume root: it is reported in `Failures` rather than answered for the whole volume containing it. |
| `options` | `GetItemsOptions?` | `null` means `GetItemsOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Cancels the wait. |

**Returns** a `RecycleBinContents`. The answer's order is the order the volumes were asked about and does
not depend on which one finished first.

A path inside a volume names no volume of its own, and it is refused rather than widened
to the volume containing it — as `EmptyAsync` refuses it and `GetStatusAsync` answers it with
`VolumeNotFound`. Refused rather than answered empty, because an empty listing is a claim about the bin
and a caller whose root was one character off cannot tell that from a bin that really is empty.

### `RestoreAsync`

Puts entries back where their own records say they were deleted from. Two overloads, taking the same
arguments after the first:

| Parameter | Type | Description |
| --- | --- | --- |
| `items` | `IReadOnlyList<RecycleBinItem>` | Entries to restore, as a listing returned them. Repeated entries are restored once, the second one reported. |
| `volumes` | `IReadOnlyList<RecycleBinVolumeContents>` | The volume groups of a listing, or any subset of them. Flattened group by group, in the order given, and restored as one batch. |
| `options` | `RestoreOptions?` | `null` means `RestoreOptions.Default`. `DirectoryConflict` and `FileConflict` decide what happens to a path that is already occupied. |
| `cancellationToken` | `CancellationToken` | Cancels the work between entries and between volumes. A cancelled restore raises and returns nothing: what was already put back is found by listing the bin, not by reading a result. |

**Returns** a [`RecycleBinRestoreResult`](#recyclebinrestoreresult), **never throws** for a per-entry
problem, and applies no version gate: a restore reads records and moves payloads, so it is as available on
a machine too old to *write* a record as `GetItemsAsync` is. A merge that has to put a file back writes one,
and it is this library's version 2 like any other — so on such a machine that entry is restorable through
this library and not through Explorer's own Restore.

The payload is moved back first and its record deleted second. That order is not interchangeable: the
other one leaves a file in the Recycle Bin that nothing describes — no listing shows it, and no
restore can put it back. This order's failure is the opposite shape and is recoverable: the file is
already back where it belongs, its entry is still in the bin, and the result says so.

A destination whose parent folders are gone is made on the way for anything actually put down. An
entry the policy leaves in the bin makes nothing.

A move the file system refuses is **tried again**, up to `MaxAttempts` times, and what is in the way is
asked about on each attempt rather than decided once before the first: a destination somebody lets go of half
way through is restored, and one that stays held is reported carrying the number of attempts it was actually
given in [`RecycleFailure.Attempts`](#recyclefailure). `OverallTimeout` bounds all of that, and it is **one
budget for the whole call** — not one per entry and not one per volume — so a run of refused entries cannot
each spend a budget of their own. That waiting is inside the call and inside `Elapsed`: with the defaults
a single entry the file system keeps refusing waits a little over a second and a half before it is given
up on, and with the default 30-second budget somewhere between sixteen and twenty-four entries can wait
that long before every entry after them is attempted once and answered at once. **Set `MaxAttempts` to
what a refusal is worth waiting for** — `MaxAttempts = 1` answers the first refusal without waiting at
all.

**Exceptions** `ArgumentNullException` when the list is null. `ArgumentException` when one of the
volume groups is null: a group that is not there has no entries to restore and no volume root to name,
so there is no path to report it at and nothing to say which volume could not be answered — and nothing
is attempted, so the groups beside it are untouched. `OperationCanceledException` when the token is
cancelled. Per-entry problems are data, not exceptions.

> **Overload note.** The two overloads take a first parameter of different types, so an **untyped** empty
> collection expression does not compile: `RestoreAsync([])` applies to both and C# cannot choose between
> them. Name the list — `IReadOnlyList<RecycleBinItem> none = [];` or `Array.Empty<RecycleBinItem>()` —
> and the call resolves. An empty list either way is a call that succeeds and does nothing.

### `EmptyAsync`

Empties the Recycle Bin.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to empty, or `null` for every volume, in which case every mounted Fixed or Removable volume is covered in a single call. A path inside a volume is not a volume root: it is reported in `Failures` rather than emptied as that whole volume. |
| `cancellationToken` | `CancellationToken` | Cancels the sweep. The sweep stops between entries and raises, so a caller who cancels gets no result at all, and what was removed is found by reading the bin rather than by reading the result. |

**Returns** a `RecycleBinEmptyResult`.

The counts are exact rather than measured: every entry's own record carries its size, so the
totals are summed from the records being deleted. That also means a record whose payload is already gone
is removed and counted. Neither figure is ever negative.

`VolumesEmptied` and `Failures` are in the order the volumes were covered, not the order the disks
finished. When several volumes are covered at once — `volumeRoot` is `null`, so the call reaches every
mounted Fixed or Removable volume — each volume is written independently and the result is assembled
afterwards, so the order is the one a caller can rely on across runs.

### `GetStatusAsync`

Whether the Recycle Bin is in use for one volume.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string` | Volume root to inspect. Must be a volume root, not a directory inside one. |
| `cancellationToken` | `CancellationToken` | Checked before the work starts. |

**Returns** a `RecycleBinStatus`. **Throws** `ArgumentException` for a null or blank path.

`GetStatusAsync` is the authoritative query on this, and `RecycleAsync` asks it before every batch —
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

### `RecycleBin`

The entry point. A static class with the seven methods above. Work runs on the thread pool, so a caller
never blocks inside the bin.

### `RecycleBinOptions`

The parent of the three types below, and the storage the engine reads. It is neither constructible nor
derivable outside the library, so a caller's options value is always one of the three and always carries
only the properties that call honours.

### `RecycleOptions`

What `RecycleAsync` takes, on either overload. Immutable; set with object initialisers. Every property has
a production default, so `RecycleOptions.Default` is always a valid argument.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `RecycleOptions` | — | Shared instance carrying the defaults. |
| `MaxAttempts` | `int` | `6` | Attempts per item before it is reported as failed. Clamped to at least 1. |
| `InitialRetryDelay` | `TimeSpan` | `50ms` | Delay before the first retry; it doubles each attempt, with up to 20 % jitter. Negative values clamp to zero. |
| `OverallTimeout` | `TimeSpan` | `30s` | Upper bound on wall-clock time spent retrying the batch. A backoff that does not fit the remaining budget ends the loop immediately rather than starting an over-budget round. |
| `ThrowOnError` | `bool` | `true` | Whether a failure raises instead of being returned. |
| `NoRecycleBinAction` | `NoRecycleBinAction` | `Throw` | What happens to a file whose volume has no Recycle Bin to put it in. |

```csharp
// Every property has a production default, so RecycleOptions.Default — which is what a null
// options argument means — is always a valid argument.
var options = new RecycleOptions
{
    MaxAttempts = 10,
    InitialRetryDelay = TimeSpan.FromMilliseconds(100),
    OverallTimeout = TimeSpan.FromSeconds(5),
    ThrowOnError = false,
    NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
};

// ThrowOnError is the single-path switch: with it off a refusal comes back as false rather than
// raising. A batch reports its per-item failures either way.
Console.WriteLine(options.OverallTimeout);
```

### `RestoreOptions`

What `RestoreAsync` takes, on either overload. Immutable; set with object initialisers. Every property has
a production default, so `RestoreOptions.Default` is always a valid argument.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `RestoreOptions` | — | Shared instance carrying the defaults. |
| `MaxAttempts` | `int` | `6` | Attempts per item before it is reported as failed. Clamped to at least 1. |
| `InitialRetryDelay` | `TimeSpan` | `50ms` | Delay before the first retry; it doubles each attempt, with up to 20 % jitter. Negative values clamp to zero. |
| `OverallTimeout` | `TimeSpan` | `30s` | Upper bound on wall-clock time spent retrying the batch. A backoff that does not fit the remaining budget ends the loop immediately rather than starting an over-budget round. |
| `FileConflict` | `RestoreFileConflict` | `Skip` | What a restore does with a **file** whose path is already occupied. See [`RestoreFileConflict`](#restorefileconflict). |
| `DirectoryConflict` | `RestoreDirectoryConflict` | `MergeSkip` | What a restore does with a **directory** whose path is already occupied. See [`RestoreDirectoryConflict`](#restoredirectoryconflict). |

A directory can be **set down whole under a new name**; a file only ever under its own. Merging a tree
into what is already there is a question a file cannot be asked, which is why these are two properties
rather than one. A restore is a batch, so a conflict is data — the resolved ones in `Conflicts`, and the
ones an `Error` policy refuses in `Failures` as well. No single entry of a restore raises, and there is no
`ThrowOnError` here for that reason.

```csharp
// Every property has a production default, so RestoreOptions.Default — which is what a null
// options argument means — is always a valid argument.
var options = new RestoreOptions
{
    MaxAttempts = 10,
    InitialRetryDelay = TimeSpan.FromMilliseconds(100),
    OverallTimeout = TimeSpan.FromSeconds(5),
    FileConflict = RestoreFileConflict.Rename,
    DirectoryConflict = RestoreDirectoryConflict.RenameDirectory
};

Console.WriteLine(options.OverallTimeout);
```

### `GetItemsOptions`

What `GetItemsAsync` takes. Immutable; set with object initialisers. `GetItemsOptions.Default` is always a
valid argument.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `GetItemsOptions` | — | Shared instance carrying the defaults. |
| `PreloadItemDetails` | `bool` | `false` | Read every entry's record during `GetItemsAsync` instead of leaving each entry to read its own on first access. Off by default, because a listing reads no records at all that way. See [Lazy fields](#lazy-fields). |

```csharp
// Every property has a production default, so GetItemsOptions.Default — which is what a null
// options argument means — is always a valid argument.
var options = new GetItemsOptions { PreloadItemDetails = true };

Console.WriteLine(options.PreloadItemDetails);
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
| `Attempts` | `int` | Rounds the path went through. `0` means nothing was attempted — never validated, its volume cannot recycle it, it names its volume by GUID, or it was removed outright on your instruction; and while restoring, an entry whose record could not be read, or one you named twice that the first had already answered. |
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
| `RestoreFailed` | Only reachable while restoring. An entry's payload would not move, its record would not go, or its record could not be read to say where it belonged — in which case `Path` is the entry's own record inside the bin, the only path it carries without reading that record. A merge reports a refusal of a file of its tree under **that file's own path** rather than the directory's, under any of the three `Merge*` values and whichever way the merge was settling that file. **A file that refused both — the move, and then the put-back that would have taken it out of the payload — accounts for two records under the same path, and the directory accounts for a third under its own**, each a separate refusal with its own code; group by path rather than counting if you want one failure per file. `RenameDirectory` sets the tree down whole under a free name, and `Skip` and `Error` leave the entry in the bin; the `Merge*` values fail on the collision itself when the destination holds a **file** — see [`RestoreDirectoryConflict`](#restoredirectoryconflict). Whatever the policy, the entry can also reach this stage for reasons of its own — an unreadable record, a payload that will not move, a record left behind a payload that already moved — and it is still in the Recycle Bin when it does, so a second attempt is worth making. The exception is that last one, where the file is already on disk and the next attempt meets a conflict instead. |
| `RestoreConflictRefused` | Only reachable while restoring, and only under `FileConflict` or `DirectoryConflict` set to `Error`. The path was already occupied, nothing on disk was touched, and the entry stays in the bin. Every other policy that answers an occupied path settles it and reports what it did in `Conflicts`, and nothing else does — `Skip` under either policy, the two renames `Rename` and `RenameDirectory`, and the three `Merge*` values, whose [reporting granularity differs per policy](#recyclebinrestoreresult). Where it holds a **file** the merge values have no tree to merge into and fail at [`RestoreFailed`](#recyclefailurestage) instead, where `RenameDirectory` takes a free name. So this stage is what opting into `Error` gets instead. `Attempts` and `HResult` are both `0`: the refusal is settled before any move. **None of those policies puts an entry in `Failures` *because of* the conflict — that is the whole difference `Error` makes, and it does not follow that such a restore reads `Succeeded`: what it does about everything else can still fail one, a record that could not be read, a payload that moved and a record that would not go, a merge whose put-back or whose payload removal was refused. A merge is the plain case of it, having reported what it settled and then failing to deal with one file; its conflicts are unchanged by that failure and both lists carry it.** **Two kinds of policy reach no conflict at all: `FileConflict = Replace`, which takes the occupant's place — where the occupant is a file, so there is nothing left to report a conflict about, though a destination holding a *directory* has nothing to overwrite and that one fails at `RestoreFailed` instead — and a `Merge*` value whose destination holds a file, which fails rather than settling anything.** |

`HResult` carries the file system's code where the file system refused, or `0` where nothing was
attempted. Test `(HResult & 0x80000000) != 0` to tell a refusal from a success code.

### `NoRecycleBinAction`

What to do with a path on a volume that cannot recycle it.

| Value | Effect |
| --- | --- |
| `Throw` | Default. The path is refused, the file stays where it is, and nothing is attempted. Raises only when `ThrowOnError` is also on. |
| `AllowIndeterminate` | The path is recycled even though the volume's answer was not `Enabled` — the answer was `Indeterminate` or `NoRecycleBinOnVolume`. A Recycle Bin is created for that volume and the file lands in it, so the volume gains one it did not have. |
| `DeletePermanently` | The file is removed for good by the library itself and listed in `PermanentlyDeletedPaths`, never in `RecycledPaths`. A file system that refuses reports `PermanentDeleteFailed` and leaves the file in place. |

The values run from safest to most destructive, and `ThrowOnError` decides how a refusal surfaces —
but only on the single-path overload, where with it off the call returns `false` rather than raising.
A batch reports the refusal in `Failures` however it is set.

Every distinct volume root in a batch is inspected once, so a batch of twenty thousand files on one
volume does not ask twenty thousand times.

### `RecycleBinItem`

One entry in the Recycle Bin. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `OriginalPath` | `string` | Full path the entry was deleted from. Read from the entry's record on first access — see [Lazy fields](#lazy-fields). |
| `DeletedAt` | `DateTimeOffset` | When it was deleted, in the local offset. Read from the same record, on the same first access. |
| `Size` | `long` | Size in bytes. `0` for a directory — its contents are not measured. |
| `IsDirectory` | `bool` | Whether the entry is a directory. |

There is no `Name`: it is the last component of `OriginalPath`.

**An entry is put back by handing it to [`RestoreAsync`](#restoreasync).** There is still no
`RecycledPath`: the path an entry came from *is* `OriginalPath`, and a restore moves the payload back
there, which is what Explorer's own Restore does over the same records.

#### Lazy fields

`Size` and `IsDirectory` are answered by the listing. `OriginalPath` and `DeletedAt` each read the
entry's own record, on the first access to either — one read answers both. So a listing of eighteen
thousand entries costs no record reads at all, and a caller that touches every entry pays for the
reads it actually asks for.

That means accessing these two fields is I/O, and it can fail:

- An entry whose record was collected under it — an empty running against a caller holding a listing — raises
  `IOException`. The exception is cached, so a second access raises the same one rather than retrying.
- Set [`PreloadItemDetails`](#getitemsoptions) to read every record during the listing instead. An entry
  whose record cannot be read is then reported in `RecycleBinContents.Failures` and left out of that volume's
  group in `Items`, rather than being returned and raising later.

#### What a listing does not show

An entry appears only when both halves of it are there. An entry whose payload is gone has no file to
report a size for, and an entry whose record is gone has no path to answer with — so neither is
listed, and neither is a failure. Both are states a Recycle Bin is briefly in, and the next
`EmptyAsync` collects what is left of them.

The listing does not filter by original path when you name a volume: it covers exactly the volumes you
asked for, so an entry it returns was deleted from one of them. Matching on `OriginalPath` instead
would mean reading every record the listing had just declined to read.

**Match this field against your own paths without regard to case.** `OriginalPath` is the path as it
was recorded, and the drive letter is written in whichever case Windows likes: an entry deleted from a
volume the caller reached for as `x:\` comes back as `X:\…`. The library does not rewrite it. Windows
paths are case-insensitive, so a caller comparing this field with a path it holds compares it that
way — and an ordinal comparison of the same pair reports a correct listing as empty.

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
| `VolumeRoot` | `string` | Volume root the entries were read from, such as `"X:\\"`. |
| `Items` | `IReadOnlyList<RecycleBinItem>` | The entries that volume's bin holds, in the order its directory gave them. Empty for a volume whose bin is empty. |

The grouping keeps a caller able to tell which volume an entry came from. The order is the order the
volumes were asked about and does not depend on which volume's walk finished first.

#### Loading every item's details

A listing reads no records at all, and each entry reads its own on first use. That is the right shape when you
want a few of them and it is the wrong one when you want all of them: the reads then happen one at a time, from
inside a loop that also has to do something else.

`LoadItemDetails` moves that cost into one batch:

- Items whose details are already in memory are skipped — every item of a listing taken with
  `PreloadItemDetails`. A listing with nothing pending does one pass over its items and no
  file reads. An item whose record has already failed to read is not "already read": it is tried again, because
  the record may be back.
- Volumes are read one after another rather than together, and each at whatever parallelism its own
  disk answers for.
- An item whose record cannot be read is **left cold**, and still raises `IOException` when a caller asks for
  its details. Nothing here throws for one, and `Failures` does not change — it describes what the listing could
  not do.
- The items themselves are the same objects the listing returned, so `TotalSize` and any reference you already
  hold stay valid.
- **It blocks the calling thread.** Every other entry point here is awaited; this one returns when it is
  done. Do not call it on a UI thread — on a bin of eighteen thousand entries that is seconds of frozen
  UI.

Raising `OperationCanceledException` is the one thing it does throw, when the token is cancelled part way; items
read before that point stay read.

### `RecycleBinEmptyResult`

The outcome of an empty. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `VolumesEmptied` | `IReadOnlyList<string>` | Volume roots covered, each with a trailing separator. In the order they were covered, not the order the work finished. |
| `ItemsRemoved` | `long` | Entries that left the Recycle Bin. Measured, never negative. |
| `BytesFreed` | `long` | Bytes they occupied. Measured, never negative. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Volumes that could not be emptied or measured. In the same order, one volume's failures together. |
| `Elapsed` | `TimeSpan` | Wall-clock time the caller waited. |

### `RecycleBinRestoreResult`

The outcome of a restore. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `RestoredPaths` | `IReadOnlyList<string>` | Where each entry now is, in full, in the order it was asked for. |
| `Conflicts` | `IReadOnlyList<RecycleRestoreConflict>` | Paths that were already occupied, and what was done about them. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Entries that did not come back. Empty when `Succeeded`. |
| `ItemsRestored` | `long` | Entries that left the Recycle Bin. Never negative, never more than the number handed in. |
| `RestoredSize` | `long` | Bytes those entries carried back, taken from their own records rather than measured afterwards. |
| `Elapsed` | `TimeSpan` | Wall-clock time the caller waited, including queueing behind other work. |
| `Succeeded` | `bool` | `true` when nothing failed — decided by `Failures` alone, and not by `Conflicts`. |

Each list keeps the relative order the entries were handed in, so a caller reading two of them can still
say which came before which. It is **not** an index you can line up against the input: a list holds only
the entries that belong in it, so one entry of three may appear in none of the three.

`Succeeded` is `true` for a restore that skipped everything it was asked about. An entry left in the bin
because its path was taken is the answer `Skip` asks for, not a failure to report it as one; only the two
`Error` policies move an entry into `Failures` **because of the conflict it resolved**. That is a statement
about the policy, not about the outcome: a restore that has reported a conflict can still read `false`, and
what it does about everything else is what decides it. A conflict never makes `Succeeded` false; a failure
does, wherever it came from.

`Conflicts` is not one record per entry you handed in. A directory merged into one that is already there
reports one record per file it settled, which under `MergeSkip` means one record for the whole tree however
many files gave way — each of those is a bin entry rather than a record — and one per overwritten or renamed
file under the other two. A tree of ninety collisions under `MergeReplace` or `MergeRename` is ninety records
and one entry; under `MergeSkip` it is one record, ninety entries back in the bin, and nothing else. A tree
that collides with nothing reports nothing at all. They sit with the directory's own entry in the order you
handed the entries in; which files of one tree come first follows the order the filesystem hands them over,
which is not a property of the tree. `RestoredPaths` is unaffected: it names what you handed in, so a merged
directory is one path on that list however many files it settled, and a file **a merge** set down under a
free name is named in `Conflicts` and nowhere else.

**An entry that was put back where its record says is reported under that path — your own spelling from
the day it was deleted, trailing separator and all.** The payload is moved to the trimmed name, because a
move is given a name rather than a separator, so `File.Exists` is false of a restored *directory* whose
record carries one and `Directory.Exists` is the check that answers for it. Every other path this library
reports is the caller's own spelling kept verbatim, and a record is the caller's own bytes. A renamed
entry is the other reading of the same rule: it is reported under the free name it landed at, because that
is where it is.

Nothing here is undone afterwards: an entry that was overwritten, or set down under a free name beside what
was in its way, stays that way, and this result is the only place that says so.

### `RecycleRestoreConflict`

One path that was already occupied when a restore reached it, and what was done about it. Reported rather
than raised — a conflict is an outcome the caller asked about by choosing a policy, and a batch that hits
one carries on with the entries after it.

| Member | Type | Description |
| --- | --- | --- |
| `Path` | `string` | The path the item wanted — except under `RestoredRenamed` and `MergedRenamed`, where it is where the item landed. The path it collided at is then named by no record in the result. The item is in `RestoredPaths` only when it is the entry that was handed in, which is true of `RestoredRenamed` and **not** of `MergedRenamed`: that one names a file *inside* a merged directory, and `RestoredPaths` holds the directory's one path. |
| `OccupiedByDirectory` | `bool` | Whether what stood in the way is a directory rather than a file. Always about the collided path, never about the free name the item was set down under. |
| `Action` | `RecycleRestoreConflictAction` | What was done about it. |

One action rather than one per policy, because a caller reads this to answer a single question — what
was in the way, and what was done about the item that wanted it.

#### `RecycleRestoreConflictAction`

| Value | Meaning |
| --- | --- |
| `Skipped` | Nothing on disk was touched and the entry is still in the Recycle Bin. This is also what an `Error` policy reports, in `Conflicts` rather than in `Failures`. |
| `RestoredRenamed` | The entry was set down whole beside the occupied path, under the first free name. |
| `MergedSkipped` | A merge that left a file which was in the way alone, the incoming file going back into the Recycle Bin as an entry of its own under the path it wanted. Under `MergeSkip` — the default — this is **one record for the directory rather than one per file that gave way**, because nothing about those files was resolved here: each is an ordinary entry in the next listing. `Path` is the directory's path, which is also in `RestoredPaths` when the merge finished. |
| `MergedReplaced` | A merge that overwrote a file that was in the way, one record per file, under `MergeReplace`. `Path` is the path it was overwritten at. |
| `MergedRenamed` | A merge that set an incoming file down under a free name of its own, `report.txt` becoming `report (1).txt`, one record per file, under `MergeRename`. `Path` is where that file ended up rather than where it collided. |

### `RestoreFileConflict`

What a restore does with a **file** whose original path is already occupied.

| Value | Effect |
| --- | --- |
| `Skip` | The default. The entry stays in the Recycle Bin and nothing on disk is touched — and the incoming file is the one that gives way, so what was already there is never what is lost. It is the value that leaves the entry in the bin as a plain answer rather than as something to be told about as well; `Rename` sets the file down beside the path under a free name, and `Replace` takes the path itself. |
| `Replace` | The file already there is overwritten. That is your own file and your own instruction; it does not extend to a directory, which is never replaced whole. |
| `Rename` | The entry is set down beside the occupied path under the first free name. The number starts at one and the extension is kept, splitting on the last dot, so `archive.tar.gz` becomes `archive.tar (1).gz`. Nothing already on disk is renamed, moved or removed. |
| `Error` | As `Skip`, and the entry is reported in `Failures` as well — which is the whole difference, and makes `Succeeded` `false` for a restore that did nothing else wrong, while the operation still carries on with the entries after it. Anything else that fails, fails under `Skip` as well: a record that could not be read, or a payload that moved and whose record would not go. |

A destination occupied by the *other* kind is a conflict like any other, and the policy applied is the
one for the kind arriving. So `Replace` onto a directory has nothing to overwrite and is reported as a
failure at stage `RestoreFailed`, with everything on disk left exactly as it was.

### `RestoreDirectoryConflict`

What a restore does with a **directory** whose original path is already occupied.

| Value | Effect |
| --- | --- |
| `MergeSkip` | The default. The tree's contents are put into the directory already there and every file of the same name already there is left exactly as it is. A file that does not fit goes back into the Recycle Bin as an entry of its own under the path it wanted, rather than being lost with the payload it was in, and the directory is reported once in `Conflicts` as `MergedSkipped` — one record rather than one per file that gave way, because nothing about those files was resolved here. |
| `MergeReplace` | The same merge, overwriting each file of the same name already there and merging a subdirectory of the same name rather than replacing it: the only way to replace a directory is to delete what is there first and find out afterwards whether the move was possible, and those bytes are yours. Each overwritten file is reported in `Conflicts` as `MergedReplaced`, under the path it was overwritten at. |
| `MergeRename` | The same merge, setting each incoming file that clashes down under a free name of its own. Each is reported in `Conflicts` as `MergedRenamed`, under the name it landed at. |
| `RenameDirectory` | The whole tree is set down beside the one already there under a free name, contents merged with nothing. The tree arrives as it was, so nothing inside it has to agree with anything outside it. |
| `Skip` | The entry stays in the Recycle Bin and nothing on disk is touched — not even the missing parent folders, so an item that is not put back leaves nothing behind. |
| `Error` | As `Skip`, and the entry is reported in `Failures` as well — which is the whole difference, and makes `Succeeded` `false` for a restore that did nothing else wrong. |

No value writes outside the directory you named, and none touches a directory the restore was not
restoring: what is already on disk is left alone, overwritten, or set beside — never renamed, moved, or
removed outright. A destination occupied by a **file** is a conflict like any other: the three `Merge*` values have no
tree to merge into, so the move is refused and reported at [`RestoreFailed`](#recyclefailurestage) with the
file left exactly as it was, where `RenameDirectory` takes a free name instead.

A destination nothing occupies is a whole-directory move, not a walk of what is inside it. **A destination
that *is* occupied by a directory is a walk.** Windows will not move a directory onto a directory, so a
`Merge*` value puts the tree's contents into what is there one entry at a time: a directory of the tree
whose own name is free moves whole — which is what carries an empty subdirectory across — one whose name is
taken is merged into, and a file whose name is taken is settled against the policy and reported. The other
three directory policies do not walk at all: `RenameDirectory` sets the tree down whole under a free name,
and `Skip` and `Error` leave the entry in the bin.

A merge reaches no failure stage of its own. What it can fail at is a file the filesystem would not take —
reported at stage [`RestoreFailed`](#recyclefailurestage) under that file's own path, and the file goes back
into the Recycle Bin as an entry of its own, under the path it wanted, where a later `RestoreAsync` can
bring it back. If even that could not be done, the payload is left whole rather than deleted with a file
inside it, and the directory's own entry stays in the bin.

**A merge can also fail having dealt with every file in its tree.** If the removal of the payload, or the
deletion of the record after it, is what the filesystem refuses — both happen after the walk has finished —
then there is no file to name, and the entry's own record is the only one in `Failures`.

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
read. A volume Windows has never configured a bin for is answered `Indeterminate`, not `Enabled` —
there is no setting for it either way, and saying "enabled" would be a guess.

Nothing is cached between calls: a volume mounted, or a policy applied, after one status check is
seen by the next.

---

## Examples

Every example below is compiled as the body of a method in
`tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs`, so what is shown here is what was built
against. They are in the order they appear in that file.

### Recycle one file

```csharp
using RecycleBinToolkit;

// RecycleAsync needs Windows 10 version 1607 (build 14393) or later, because that is the first build
// that reads the index records it writes; below that the call raises
// PlatformNotSupportedException, and ThrowOnError does not suppress that.
await RecycleBin.RecycleAsync(@"W:\Users\me\Downloads\archive.zip");

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
// A listing reads no entry records by default, which is what makes one walk of the bin directory
// enough. A caller that is going to read every entry's path anyway can have the records read during
// the listing instead of one at a time from its own loop — and an entry whose record cannot be read
// is reported in Failures rather than raising later.
var eager = new GetItemsOptions { PreloadItemDetails = true };

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

### Restore one item, and report what was in its way

The [overloads](#restoreasync) are batch-shaped like the three above, so a restore reports rather than
raises: what came back, what something was already sitting on, and what did not move at all.

```csharp
var contents = await RecycleBin.GetItemsAsync(@"W:\");

// One entry of one volume, out of a listing that holds something.
var entry = contents.Items[0].Items[0];

// A path somebody took again while the entry sat in the bin is the ordinary collision. Rename sets
// the entry down beside it under the first free name; Skip would leave it in the bin, and Error
// would report it as a failure. A directory is its own question — see DirectoryConflict.
var options = new RestoreOptions { FileConflict = RestoreFileConflict.Rename };

var result = await RecycleBin.RestoreAsync([entry], options);

Console.WriteLine($"{result.ItemsRestored} item(s), {result.RestoredSize} byte(s), in {result.Elapsed.TotalMilliseconds:F0} ms");

// Succeeded is the failures list alone. An entry left in the bin because its path was taken is the
// answer Rename asked for, so it is reported here rather than raised.
foreach (var path in result.RestoredPaths)
{
    Console.WriteLine($"{path} is back");
}

foreach (var conflict in result.Conflicts)
{
    // Under RestoredRenamed this is where the entry landed, not the path it collided with — which
    // no record names. Under Skipped it is the path that was occupied.
    Console.WriteLine($"{conflict.Path} was already there: {conflict.Action}");
}

foreach (var failure in result.Failures)
{
    // Still in the bin, and still worth a second attempt: nothing here is undone.
    Console.Error.WriteLine($"{failure.Path} did not come back: {failure.Stage} 0x{failure.HResult:X8}");
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
var result = await RecycleBin.RecycleAsync(paths);

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
var result = await RecycleBin.RecycleAsync(paths);

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
var result = await RecycleBin.RecycleAsync(paths);

Console.WriteLine($"{result.RecycledPaths.Count} path(s) recycled");
```

### Never let a file be destroyed without a decision

```csharp
IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];
var options = new RecycleOptions
{
    NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
};

var result = await RecycleBin.RecycleAsync(paths, options);

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
await RecycleBin.RecycleAsync(paths, cancellationToken: cts.Token);
```

### Refuse a batch on a volume without a Recycle Bin

```csharp
IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];

// The default NoRecycleBinAction is Throw, so on a volume with no Recycle Bin the path is refused
// before anything is attempted — and because a batch reports every per-item failure rather than
// raising, the refusal arrives in Failures.
var result = await RecycleBin.RecycleAsync(paths);

foreach (var failure in result.Failures)
{
    Console.Error.WriteLine($"{failure.Path} was not recycled: {failure.Stage} 0x{failure.HResult:X8}");
}
```

### Recycle one path without throwing

```csharp
// ThrowOnError is the single-path switch: with it off a refusal comes back as false
// instead of raising, and the file is left where it was.
var options = new RecycleOptions { ThrowOnError = false };

bool gone = await RecycleBin.RecycleAsync(@"W:\Reports\quarterly.xlsx", options);
```

### Assert in a test

`Check.*` here is the test project's own assertion helper, and it is what the block below reads in
`ReadmeExamples.cs` too — the two are character for character. In a project with xunit the same three
lines read `Assert.True`, `Assert.Equal` and `Assert.False`.

```csharp
IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
var result = await RecycleBin.RecycleAsync(paths);

Check.True(result.Succeeded, "the batch succeeded");
Check.Equal(0, result.Failures.Count, "no failures");

// Succeeded is derived from the library's own checks; File.Exists is the independent one.
foreach (var path in result.RecycledPaths)
{
    Check.False(File.Exists(path), $"{path} is gone");
}
```

---

## Contributing

Every code block in this file also exists as a compiling method in
`tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs`, and the two are edited together. A new example
goes into both in the same change; the rule is spelled out in `AGENTS.md` at the repository root.

This file answers what the library is, why you would reach for it, and what it will do. How it works
and why it was built that way is `AGENTS.md`, and what the tests cover is in the test project. New
behaviour should arrive with a test that was seen to fail first.

## License

No license has been chosen yet. Choose one before publishing — until then the code is unlicensed and
not usable outside this repository.
