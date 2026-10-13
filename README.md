# RecycleBinToolkit

A high-performance .NET library for the Windows Recycle Bin: move files and directories into it, list
what is in it, restore entries, empty it, and report the outcome of every item. It reads and writes the
Recycle Bin's own files — no `SHFileOperation`, no `IFileOperation`, no COM.

[![NuGet](https://img.shields.io/nuget/v/RecycleBinToolkit.svg)](https://www.nuget.org/packages/RecycleBinToolkit) [![NuGet](https://img.shields.io/nuget/dt/RecycleBinToolkit.svg)](https://www.nuget.org/packages/RecycleBinToolkit)

```csharp
using RecycleBinToolkit;

await RecycleBin.RecycleAsync(@"W:\Users\me\Downloads\archive.zip");

await RecycleBin.GetItemsAsync();
await RecycleBin.EmptyAsync(@"W:\");
```

- **Targets** `net10.0`, Windows only. The library is `AnyCPU`.
- **Dependency**: `Microsoft.Windows.CsWin32` only, and it is a build-time source generator.
- Every method on `RecycleBin` is safe to call from any thread.

## What it is

Every item is moved, reported and retried on its own:

- A path that cannot be validated, and a path on a volume that cannot recycle it, are refused before
  anything is attempted, and the file is left untouched.
- A failing item does not abort a batch, and does not cause the items that already succeeded to be
  worked again.
- The batch overload of `RecycleAsync` and both overloads of `RestoreAsync` report a per-item failure as
  data and raise nothing for it.
- A permanent delete happens only where `NoRecycleBinAction.DeletePermanently` or
  `LongPathFailureAction.DeletePermanently` asks for one, and is reported in `PermanentlyDeletedPaths`.
- A path the Recycle Bin cannot record is either refused with the file left in place, or handled by
  `LongPathFailureAction`. See [Long paths](#long-paths).
- `RestoreAsync` puts an entry back where its own record says it was deleted from, and reports per
  entry in [`RecycleBinRestoreResult`](#recyclebinrestoreresult).

---

## Requirements

| | |
| --- | --- |
| OS | Windows |
| Runtime | .NET 10 |
| Architecture | Any |
| Permissions | Write access to the file, and to its parent directory |

The library writes whichever of the Recycle Bin's two index formats this machine's own Windows reads,
so the same call works on an old build and on a current one.

The API is annotated `[SupportedOSPlatform("windows10.0.14393")]`, so calling it from a
cross-platform target produces an analyzer warning unless you guard with
`OperatingSystem.IsWindows()`.

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
var paths = Directory.GetFiles(@"W:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

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

Entries that may be null or missing:

```csharp
IReadOnlyList<string?> paths = [];

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
| `RecycleBin.EmptyAsync(string? volumeRoot, EmptyOptions? options, CancellationToken cancellationToken = default)` | `Task<RecycleBinEmptyResult>` | The same, reporting each entry as it goes. `volumeRoot` has no default on this overload, so `EmptyAsync(root)` still picks the one above. |
| `RecycleBin.GetStatusAsync(string volumeRoot, CancellationToken cancellationToken = default)` | `Task<RecycleBinStatus>` | Whether it is in use for a volume, and why not if it is not. |

`GetItemsAsync`, `RestoreAsync`, `EmptyAsync` and `GetStatusAsync` report an environmental problem as
data in the result rather than raising it, so a batch over several volumes survives one that is not
mounted.

### `RecycleAsync(string?, RecycleOptions?, CancellationToken)`

Recycles a single file or directory.

| Parameter | Type | Description |
| --- | --- | --- |
| `filePath` | `string?` | Path of the file or directory. |
| `options` | `RecycleOptions?` | Retry and error-handling settings. `null` means `RecycleOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Cancels the scan that validates the path and the work itself. |

**Returns** `true` when the path is gone from disk, whether it was recycled or removed permanently
under an explicit policy.

**Exceptions**

| Exception | When |
| --- | --- |
| `ArgumentException` | `filePath` is null, empty or whitespace, and `ThrowOnError` is on |
| `FileNotFoundException` | `filePath` does not exist, and `ThrowOnError` is on |
| `IOException` | A move failed, or a volume refused the path, and `ThrowOnError` is on |
| `NotSupportedException` | The path names its volume by GUID, and `ThrowOnError` is on |
| `PathTooLongException` | The Recycle Bin cannot record this path, and `ThrowOnError` is on — see [Long paths](#long-paths) |
| `OperationCanceledException` | `cancellationToken` was cancelled before the work started |

The file is untouched in every one of those cases. `IOException.HResult` carries the file system's own
code, or `0` where the volume was refused from configuration. A GUID path was refused before anything
was attempted; address the same location by drive letter instead.

With `ThrowOnError` off, nothing is thrown: a bad path returns `false`.

> **Overload note.** A bare `RecycleAsync(null)` is ambiguous between the two overloads and
> does not compile. Cast it: `(string?)null`, or pass a list.

### `RecycleAsync(IReadOnlyList<string?>, RecycleOptions?, CancellationToken)`

Recycles a batch. This overload **never throws** for a per-item failure.

| Parameter | Type | Description |
| --- | --- | --- |
| `filePaths` | `IReadOnlyList<string?>` | Paths to recycle. A directory is one path and goes in as one item, contents and all. Entries may be null or missing; each is reported as a failure without aborting the batch. Repeated paths are recycled once. An empty list succeeds and does nothing. |
| `options` | `RecycleOptions?` | Retry settings, plus `NoRecycleBinAction`. `ThrowOnError` is **not read here**: the batch reports every per-item failure rather than raising. Use `RecycleAsync(string?, …)` if you want a failure to raise. `null` means `RecycleOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Cancels the scan that validates the batch and the work between paths. A cancelled batch stops part way and raises `OperationCanceledException` carrying no result: which files went is found by reading the bin. |

**Returns** a `RecycleBinResult` describing every item.

**Exceptions** `ArgumentNullException` when `filePaths` is null, `OperationCanceledException` when the
token is cancelled. Per-item problems are data, not exceptions.

### `GetItemsAsync`

Lists the Recycle Bin.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to read, such as `"W:\"`, or `null` for every volume. A path inside a volume is not a volume root and is reported in `Failures`. |
| `options` | `GetItemsOptions?` | `null` means `GetItemsOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Cancels the wait. |

**Returns** a `RecycleBinContents`, one group per volume in the order the volumes were asked about.

A path that is not a volume root is refused rather than widened to the volume containing it — on
`GetItemsAsync` and `EmptyAsync` as an entry in `Failures`, on `GetStatusAsync` as `VolumeNotFound`.

### `RestoreAsync`

Puts entries back where their own records say they were deleted from. Two overloads, taking the same
arguments after the first:

| Parameter | Type | Description |
| --- | --- | --- |
| `items` | `IReadOnlyList<RecycleBinItem>` | Entries to restore, as a listing returned them. A repeated entry is restored once and the second reported. |
| `volumes` | `IReadOnlyList<RecycleBinVolumeContents>` | Volume groups of a listing, or any subset. Flattened group by group in the order given, restored as one batch. |
| `options` | `RestoreOptions?` | `null` means `RestoreOptions.Default`. `FileConflict` and `DirectoryConflict` decide what happens to an occupied path. |
| `cancellationToken` | `CancellationToken` | Cancels the work between entries and volumes. A cancelled restore raises and returns nothing: what was put back is found by listing the bin. |

**Returns** a [`RecycleBinRestoreResult`](#recyclebinrestoreresult). Never throws for a per-entry problem.

Missing parent folders of a destination are created for entries that are actually put back. An entry the
policy leaves in the bin creates nothing.

`MaxAttempts` bounds how often a refused move is retried, and
[`RecycleFailure<TStage>.Attempts`](#recyclefailure) reports how many were given. `OverallTimeout` is one budget
for the whole call — not per entry, not per volume — and covers the waiting. `MaxAttempts = 1` answers
the first refusal without retrying.

**Exceptions** `ArgumentNullException` when the list is null. `ArgumentException` when one of the volume
groups is null: nothing is attempted, so the groups beside it are untouched. `OperationCanceledException`
when the token is cancelled. Per-entry problems are data, not exceptions.

> **Overload note.** The two overloads take a first parameter of different types, so an **untyped** empty
> collection expression does not compile: `RestoreAsync([])` applies to both and C# cannot choose between
> them. Name the list — `IReadOnlyList<RecycleBinItem> none = [];` or `Array.Empty<RecycleBinItem>()` —
> and the call resolves. An empty list either way is a call that succeeds and does nothing.

### `EmptyAsync`

Empties the Recycle Bin. The first signature below is the one to reach for unless you want reports.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to empty, or `null` for every mounted Fixed or Removable volume in one call. A path inside a volume is not a volume root and is reported in `Failures`. |
| `cancellationToken` | `CancellationToken` | Cancels the sweep. It stops between entries and raises, so a cancelled call returns no result: what was removed is found by reading the bin. |

**Returns** a `RecycleBinEmptyResult`. `ItemsRemoved` and `BytesFreed` are summed from the records
being deleted, so a record whose payload is already gone counts as removed. `VolumesEmptied` and
`Failures` are in the order the volumes were covered.

The second overload adds `EmptyOptions? options` between the two, `null` meaning `EmptyOptions.Default`.
`volumeRoot` has no default on it, so a call that names only a root still binds to the first overload.
See [Progress](#progress).

### `GetStatusAsync`

Whether the Recycle Bin is in use for one volume.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string` | Volume root to inspect. Must be a volume root, not a directory inside one. |
| `cancellationToken` | `CancellationToken` | Checked before the work starts. |

**Returns** a `RecycleBinStatus`. **Throws** `ArgumentException` for a null or blank path.

`RecycleAsync` calls this before a batch and refuses a path whose volume is not `Enabled`, unless
`NoRecycleBinAction` says otherwise. Nothing is cached: a volume mounted or a policy applied after one
call is seen by the next.

### Progress

`RecycleAsync`, `RestoreAsync` and `EmptyAsync` can report each item as it is settled. The three carry the
same three properties and nothing else; what differs is the type their handler is given, because a
recycled path, a restored entry and an emptied entry have different outcomes to report.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `OnItemProcessed` | `Action<RecycleProgress>` / `Action<RestoreProgress>` / `Action<EmptyProgress>` | `null` | Called once per item. `null` reports nothing and costs one null check per item. |
| `OnProgressFailed` | `Action<Exception>` | `null` | Called once with whatever `OnItemProcessed` threw, after which that handler is not called again. |
| `WaitForProgressCallbacks` | `bool` | `false` | Whether the call returns only once every report has been delivered. |

| Guarantee | |
| --- | --- |
| One report per input item | Includes one refused, repeated, or absent path. `RecycleAsync` and `RestoreAsync` report `Count` equal to the length of what you passed. |
| `Index` is one counter | Handed out as each item is settled, never reused within a call, and shared by every thread the operation runs on. |
| `Elapsed` is stamped at creation, not at delivery | A handler that takes a second on item 3 does not make item 4 look slow. It counts from the start of the call, not from the moment you built the settings. |
| Delivery order is arrival order | A restore across two volumes or an empty across two disks reports volume B before volume A. `Index` is the ordering, not the stream. |
| The operation never waits for the handler | It runs to completion while your handler is still working. Reports may still arrive after the `await` returns unless `WaitForProgressCallbacks` is set. |
| The handler runs on a thread of its own | Never on the thread that called the operation, and never on two reports at once. It is *not* a UI thread: marshal to the one yourself. |
| A cancelled call reports only what it decided | The method raises `OperationCanceledException` and returns no result. |
| A handler that throws ends delivery | The failure handler is told once and the operation still returns its result. It never reaches the operation. |
| `EmptyAsync` reads every bin one extra time | Only when `OnItemProcessed` is set, and only to know the total. Leave it `null` and nothing is read that the empty does not already read. |
| `EmptyAsync` may report more or fewer than `Count` | The total is counted before the sweep, so an entry added meanwhile pushes `Index` past it. Clamp your bar. |
| A restore does not report a merge's leftovers | A file a merge could not put back goes into the Recycle Bin again. It was never in your list, so it has no index in your count. See `MergedSkipped` in `Conflicts`. |
| An empty reports no path | It says whether an entry is out of the bin. Which entries would not go is in `RecycleBinEmptyResult.Failures`, named by their record inside the bin rather than by where they were. |

#### `RecycleProgress`

The report `RecycleAsync` raises for one path, carrying its position in the batch. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Index`, `Count` | `int` | This item's number, and how many the call was asked about. |
| `Status` | `RecycleStatus` | The outcome for this path. See [`RecycleStatus`](#recyclestatus). |
| `Path` | `string` | The path as you wrote it. A batch entry that was `null` is reported as `""`. |
| `Elapsed` | `TimeSpan` | From the start of the call to the moment this report was created. |
| `Failure` | `RecycleFailure<RecyclePathFailureStage>?` | Why it was refused or failed, or `null`. A repeated path is `Refused` with no `Failure`: it was not acted on, and there is no reason to invent. |

#### `RestoreProgress`

The report `RestoreAsync` raises for one entry, carrying its position in the batch. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Index`, `Count` | `int` | This entry's number, and how many the call was asked about. |
| `Status` | `RestoreStatus` | The outcome for this entry. See [`RestoreStatus`](#restorestatus). |
| `Path` | `string` | Where the entry landed, or the record it came from when it landed nowhere. |
| `Elapsed` | `TimeSpan` | From the start of the call to the moment this report was created. |
| `Failure` | `RecycleFailure<RestoreFailureStage>?` | Why it was skipped or failed, or `null`. |

#### `EmptyProgress`

The report `EmptyAsync` raises for one entry, carrying its position in the count. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Index`, `Count` | `int` | This entry's number, and how many were counted before the sweep began. |
| `Status` | `EmptyStatus` | The outcome for this entry. See [`EmptyStatus`](#emptystatus). |
| `Elapsed` | `TimeSpan` | From the start of the call to the moment this report was created. |

```csharp
var options = new RecycleOptions
{
    OnItemProcessed = progress =>
        Console.WriteLine($"{progress.Index}/{progress.Count} {progress.Status} {progress.Path}"),
    WaitForProgressCallbacks = true
};

await RecycleBin.RecycleAsync([@"W:\Users\me\Downloads\archive.zip"], options);
```

```csharp
await RecycleBin.EmptyAsync(@"W:\", new EmptyOptions
{
    OnItemProcessed = progress => Console.WriteLine($"{progress.Index}/{progress.Count} {progress.Status}")
});
```

---

## Types

### `RecycleBin`

The entry point. A static class with the seven methods above.

### `RecycleBinOptions`

The parent of the three types below. Neither constructible nor derivable outside the library, so a
caller's options value is always one of the three.

### `RecycleOptions`

What `RecycleAsync` takes, on either overload. Immutable; set with object initialisers. Every property has
a production default, so `RecycleOptions.Default` is always a valid argument.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `RecycleOptions` | — | Shared instance carrying the defaults. |
| `MaxAttempts` | `int` | `6` | Attempts per item before it is reported as failed. Clamped to at least 1. |
| `InitialRetryDelay` | `TimeSpan` | `50ms` | Delay before the first retry; it doubles each attempt, with up to 20 % jitter. Negative values clamp to zero. |
| `OverallTimeout` | `TimeSpan` | `30s` | Upper bound on wall-clock time spent retrying the batch, one budget for the whole call. |
| `ThrowOnError` | `bool` | `true` | Whether a single-path failure raises instead of returning `false`. On the batch overload the property is not read. |
| `NoRecycleBinAction` | `NoRecycleBinAction` | `Throw` | What happens to a file whose volume has no Recycle Bin to put it in. |
| `LongPathFailureAction` | `LongPathFailureAction` | `Throw` | What happens to a path the Recycle Bin cannot record. See [Long paths](#long-paths). |
| `OnItemProcessed` | `Action<RecycleProgress>` | `null` | Reports each path as it is settled. See [Progress](#progress). |
| `OnProgressFailed` | `Action<Exception>` | `null` | Told once if `OnItemProcessed` throws, after which it is not called again. |
| `WaitForProgressCallbacks` | `bool` | `false` | Whether the call waits for every report before returning. |

```csharp
var options = new RecycleOptions
{
    MaxAttempts = 10,
    InitialRetryDelay = TimeSpan.FromMilliseconds(100),
    OverallTimeout = TimeSpan.FromSeconds(5),
    ThrowOnError = false,
    NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
};

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
| `OverallTimeout` | `TimeSpan` | `30s` | Upper bound on wall-clock time spent retrying the batch, one budget for the whole call. |
| `FileConflict` | `RestoreFileConflict` | `Skip` | What a restore does with a **file** whose path is already occupied. See [`RestoreFileConflict`](#restorefileconflict). |
| `DirectoryConflict` | `RestoreDirectoryConflict` | `MergeSkip` | What a restore does with a **directory** whose path is already occupied. See [`RestoreDirectoryConflict`](#restoredirectoryconflict). |
| `OnItemProcessed` | `Action<RestoreProgress>` | `null` | Reports each entry as it is settled. See [Progress](#progress). |
| `OnProgressFailed` | `Action<Exception>` | `null` | Told once if `OnItemProcessed` throws, after which it is not called again. |
| `WaitForProgressCallbacks` | `bool` | `false` | Whether the call waits for every report before returning. |

A directory entry can be merged or set down whole under a new name; a file entry only ever takes its
own name back. A conflict is data: the resolved ones in `Conflicts`, the ones an `Error` policy refuses
in `Failures` as well. No entry of a restore raises, which is why there is no `ThrowOnError` here.

```csharp
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

### `EmptyOptions`

What the reporting overload of `EmptyAsync` takes. Immutable; set with object initialisers.
`EmptyOptions.Default` is always a valid argument. It carries the three reporting properties and nothing
else — an empty has no retry budget, no conflict policy and no threshold to raise on.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `EmptyOptions` | — | Shared instance carrying the defaults. |
| `OnItemProcessed` | `Action<EmptyProgress>` | `null` | Reports each entry as it leaves the bin. Setting it makes the empty read every volume's bin once more. See [Progress](#progress). |
| `OnProgressFailed` | `Action<Exception>` | `null` | Told once if `OnItemProcessed` throws, after which it is not called again. |
| `WaitForProgressCallbacks` | `bool` | `false` | Whether the call waits for every report before returning. |

### `GetItemsOptions`

What `GetItemsAsync` takes. Immutable; set with object initialisers. `GetItemsOptions.Default` is always a
valid argument.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `GetItemsOptions` | — | Shared instance carrying the defaults. |
| `PreloadItemDetails` | `bool` | `false` | Read every record during `GetItemsAsync` instead of leaving each entry to read its own on first access. See [Lazy fields](#lazy-fields). |

```csharp
var options = new GetItemsOptions { PreloadItemDetails = true };

Console.WriteLine(options.PreloadItemDetails);
```

### `RecycleBinResult`

The immutable result of one `RecycleAsync` batch call.

| Member | Type | Description |
| --- | --- | --- |
| `Succeeded` | `bool` | `true` when every requested path is gone from disk and nothing was stranded. |
| `RecycledPaths` | `IReadOnlyList<string>` | Paths recycled, exactly as supplied. |
| `PermanentlyDeletedPaths` | `IReadOnlyList<string>` | Paths removed permanently under an explicit policy. Empty unless that happened. |
| `LeftoverStagingDirectories` | `IReadOnlyList<string>` | Staging directories left holding something, so the caller's data is still on their disk. Empty unless that happened. |
| `Failures` | `IReadOnlyList<RecycleFailure<RecyclePathFailureStage>>` | Paths that were not handled. Empty when `Succeeded`. |
| `Elapsed` | `TimeSpan` | Time the call took. |
| `FirstFailure` | `RecycleFailure<RecyclePathFailureStage>?` | The first failure recorded, or `null`. |
| `Create(...)` | `static RecycleBinResult` | Constructs a result. |

> `Succeeded` means **gone from disk**, not necessarily recycled. A permanently deleted path counts as
> success. Check `PermanentlyDeletedPaths` when the distinction matters.

```csharp
var result = RecycleBinResult.Create(["a"], [], [], TimeSpan.Zero);

Console.WriteLine($"{result.Succeeded} {result.Elapsed} {result.FirstFailure is null}");
```

### `RecycleFailure<TStage>`

One item a call did not handle. `TStage` is the enumeration of the operation that reported it, so a
stage that operation cannot produce will not compile.

| Type argument | Reported by |
| --- | --- |
| `RecyclePathFailureStage` | `RecycleBinResult.Failures`, `RecycleProgress.Failure` |
| `ListFailureStage` | `RecycleBinContents.Failures` |
| `EmptyFailureStage` | `RecycleBinEmptyResult.Failures` |
| `RestoreFailureStage` | `RecycleBinRestoreResult.Failures`, `RestoreProgress.Failure` |

| Member | Type | Description |
| --- | --- | --- |
| `Path` | `string` | The path the failure is about, in full. |
| `HResult` | `int` | The file system's error code, or `0` when nothing was attempted. |
| `Attempts` | `int` | Attempts made for the item. `0` means nothing was attempted. |
| `Stage` | `TStage` | Where it failed, in the terms of the operation that reported it. |
| `RecycleFailure(...)` | constructor | Constructs a failure record. |

What `Path` names depends on `Stage`, not on the class. For `RecyclePathFailureStage` it is the path
you supplied; for the other three it is a volume root or a path inside a Recycle Bin, neither of
which you named.

**Reading `HResult`.** Success and failure are told apart by the severity bit, not by comparing with
zero.

```csharp
var failure = new RecycleFailure<RecyclePathFailureStage>(@"W:\x.bin", unchecked((int)0x80270027), 1, RecyclePathFailureStage.Performing);

bool moveSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
Console.WriteLine(moveSucceeded);
```

A locked file reports `0x80270027` at stage `Performing`.

### `RecyclePathFailureStage`

The step a `RecycleAsync` call stopped at for one path.

| Value | Meaning |
| --- | --- |
| `PathValidation` | The path was empty, malformed, or pointed at nothing. |
| `Performing` | The move to the Recycle Bin failed for this item. |
| `NoRecycleBinOnVolume` | The volume cannot recycle the path and the caller asked for a Recycle Bin. Nothing was attempted: `Attempts` and `HResult` are `0`, and the file is untouched. |
| `PermanentDeleteFailed` | `NoRecycleBinAction.DeletePermanently` was asked for and the file system refused — a handle is held, the attributes are read-only. The file is untouched. |
| `VolumeGuidPathNotAddressable` | The path names its volume by GUID, as `\\?\Volume{GUID}\…`, which has no drive letter to be measured, emptied or reported against. Nothing was attempted: `Attempts` and `HResult` are `0`. Address the same location by drive letter or mount point. |
| `LongPathNotRecyclable` | The Recycle Bin cannot record this path and the caller asked for it to be recycled. Nothing was attempted: `Attempts` is `0`, `HResult` is `0x800700CE`, and the file is untouched. See [Long paths](#long-paths). |

### `ListFailureStage`

The step a `GetItemsAsync` or `LoadItemDetails` call stopped at for one volume.

| Value | Meaning |
| --- | --- |
| `Enumeration` | Nothing usable came back from that volume: it could not be walked at all, or one record on it could not be read and every other entry was. `Path` names the volume root or the record, never the path the deleted item was originally at. |

### `EmptyFailureStage`

The step an `EmptyAsync` call stopped at for one volume.

| Value | Meaning |
| --- | --- |
| `Enumeration` | The volume's Recycle Bin could not be walked or read. Nothing is claimed about what is left on it, and the volume is not in `VolumesEmptied`. `Path` names the volume root or the record. |
| `EntryNotRemoved` | One entry on the volume could not be removed — something is holding it open, or this account may not remove it. It is still in the Recycle Bin, its size is not in `BytesFreed`, and the volume is not in `VolumesEmptied` because something was left behind on it. The rest of the volume was emptied. |

### `RestoreFailureStage`

The step a `RestoreAsync` call stopped at for one entry.

| Value | Meaning |
| --- | --- |
| `RestoreFailed` | A record could not be read, a payload would not move, or a record would not be deleted. See the notes below. |
| `RestoreConflictRefused` | Under `FileConflict` or `DirectoryConflict` set to `Error`: the path was occupied, nothing was touched, and the entry stays in the bin. `Attempts` and `HResult` are `0`. |

Under `RestoreFailed`:

- `Path` is the entry's own record inside the bin when the record could not be read.
- A merge reports a refused file under that file's own path, not the directory's. A file that refused
  twice can produce two failures under the same path — group by path for one failure per file.
- The entry is still in the bin, so a retry is worth making, unless the payload already moved and the
  file is on disk.

### `RecycleStatus`

The outcome of one path in a `RecycleAsync` call.

| Value | Meaning |
| --- | --- |
| `Recycled` | The path is in the Recycle Bin. |
| `Failed` | The move was attempted and did not happen. `RecycleProgress.Failure` carries why. |
| `Refused` | The path was never attempted and is still where the caller left it. A repeated path in one batch is refused too. |
| `DeletedPermanently` | The path was deleted outright rather than recycled, under `NoRecycleBinAction.DeletePermanently` or `LongPathFailureAction.DeletePermanently`. It is listed in `PermanentlyDeletedPaths`, never in `RecycledPaths`. |

Only `Recycled` and `DeletedPermanently` mean the path is gone from disk.

### `LongPathFailureAction`

What to do with a path the Recycle Bin cannot record. See [Long paths](#long-paths) for when this
applies at all.

| Value | Effect |
| --- | --- |
| `Throw` | Default. The path is refused, reported at `LongPathNotRecyclable`, and left in place. Raises `PathTooLongException` on `RecycleAsync(string?, …)` only when `ThrowOnError` is on. |
| `DeletePermanently` | The file is deleted permanently by the library itself and listed in `PermanentlyDeletedPaths`, never in `RecycledPaths`. A file system that refuses reports `PermanentDeleteFailed` and leaves the file in place. |
| `ShortenPathAndRecycle` | The item is moved to a short path on its own volume — a rename, so nothing is copied — and recycled from there. |

`ShortenPathAndRecycle` trades the original location in the Recycle Bin: the entry shows the staging
path the item was deleted from, and restoring it later puts it there rather than where it lived. An
item whose recycle fails is moved back. If the library cannot move it back either, the directory is
left in place and named in `LeftoverStagingDirectories` rather than deleted.

### Long paths

A path is recorded in the Recycle Bin, and a record can only hold so much of one. Where the format in
use counts its length, every path Windows can name fits and nothing below applies. Where it does not,
a path past about 259 characters cannot be recorded at all, and this library does the following, in
order:

| | |
| --- | --- |
| A short name the volume already has | Used in the record. The file does not move — the address changes, nothing else. Only on volumes with 8.3 short names enabled. |
| `LongPathFailureAction` | Otherwise. Nothing happens to the file without the caller saying so. |

One path that cannot be recorded never costs the caller the rest of the batch: the other paths are
still recycled.

A short name comes first, because it is the only answer that leaves the file alone. It has one
consequence worth knowing: the record names the short name, so restoring that entry later puts the file
back under it. The directory is the one it always was; the file's own name is the volume's short form
of it.

### `NoRecycleBinAction`

What to do with a path on a volume that cannot recycle it.

| Value | Effect |
| --- | --- |
| `Throw` | Default. A path whose volume cannot recycle it is refused and left in place. Raises only on `RecycleAsync(string?, …)` and only when `ThrowOnError` is on; the batch overload reports it in `Failures`. |
| `AllowIndeterminate` | The path is recycled even though the volume's answer was not `Enabled` — it was `Indeterminate` or `NoRecycleBinOnVolume`. A Recycle Bin is created for that volume, so the volume gains one. |
| `DeletePermanently` | The file is deleted permanently by the library itself and listed in `PermanentlyDeletedPaths`, never in `RecycledPaths`. A file system that refuses reports `PermanentDeleteFailed` and leaves the file in place. |

`ThrowOnError` decides how a refusal surfaces on the single-path overload only; a batch reports it in
`Failures` however it is set.

### `RecycleBinItem`

One entry in the Recycle Bin. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `OriginalPath` | `string` | Full path the entry was deleted from. Read from the entry's record on first access — see [Lazy fields](#lazy-fields). |
| `DeletedAt` | `DateTimeOffset` | When it was deleted, in the local offset. Read from the same record, on the same first access. |
| `Size` | `long` | Size in bytes. `0` for a directory — its contents are not measured. |
| `IsDirectory` | `bool` | Whether the entry is a directory. |

There is no `Name`: it is the last component of `OriginalPath`. An entry is put back by handing it to
[`RestoreAsync`](#restoreasync).

#### Lazy fields

| Member | When it is read | On failure |
| --- | --- | --- |
| `OriginalPath`, `DeletedAt` | On first access, one read for both | `IOException` if the record is gone; cached and raised again on a second access |
| `Size`, `IsDirectory` | By the listing | — |

To read every record up front instead, see [`LoadItemDetails`](#loading-every-items-details) and
[`PreloadItemDetails`](#getitemsoptions).

#### What a listing does not show

| Not listed | Why |
| --- | --- |
| An entry whose payload is gone | No size to report |
| An entry whose record is gone | No path to report |

Neither is a failure. `EmptyAsync` collects both.

The listing is not filtered by original path: every entry returned was deleted from a volume asked for.

> Compare `OriginalPath` without regard to case. It is the path as recorded, and the drive letter is
> written in whichever case Windows uses, so an ordinal comparison reports a correct listing as empty.

> One original path can appear more than once. Deleting the same file twice leaves two entries with two
> deletion times, and both are listed.

#### Equality

| | |
| --- | --- |
| Two entries are equal | When they are the same stored entry — the same record in the bin |
| What is compared | The record's own path, without regard to case. `OriginalPath` and `DeletedAt` are not read to compare |
| One file deleted twice | Two records, so two entries that are not equal. `Distinct()` keeps both |
| `Distinct()` over two listings | Collapses the entries that are the same record, so a second listing adds nothing to what you already hold |

### `RecycleBinContents`

The outcome of a listing. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Items` | `IReadOnlyList<RecycleBinVolumeContents>` | One group per volume read, in the order the volumes were asked about. A volume with an empty bin is still one of the groups. |
| `Failures` | `IReadOnlyList<RecycleFailure<ListFailureStage>>` | Entries that could not be read, at stage `Enumeration`. |
| `Elapsed` | `TimeSpan` | Time the listing took. |
| `TotalSize` | `long` | Sum of `Size` over every group's entries. Derived from `items` on construction. |
| `LoadItemDetails(CancellationToken)` | `void` | Reads every item's lazy fields now. See below. |
| `RecycleBinContents(...)` | constructor | Constructs a listing. |

#### `RecycleBinVolumeContents`

What one volume's Recycle Bin holds.

| Member | Type | Description |
| --- | --- | --- |
| `VolumeRoot` | `string` | Volume root the entries were read from, such as `"X:\\"`. |
| `Items` | `IReadOnlyList<RecycleBinItem>` | The entries that volume's bin holds, in directory order. |
| `RecycleBinVolumeContents(...)` | constructor | Constructs the group for one volume. |

#### Loading every item's details

`LoadItemDetails` reads every item's lazy fields in one batch.

| | |
| --- | --- |
| Already-read items | Skipped |
| An item whose record cannot be read | Left unread; raises `IOException` on access. `Failures` is unchanged |
| The item objects | The same instances the listing returned |
| Cancellation | `OperationCanceledException`; items read before the token was cancelled stay read |
| Threading | **Synchronous.** Not for a UI thread — a bin of 18,000 entries takes seconds |

### `RecycleBinEmptyResult`

The immutable result of one `EmptyAsync` call.

| Member | Type | Description |
| --- | --- | --- |
| `VolumesEmptied` | `IReadOnlyList<string>` | Volume roots covered, each with a trailing separator, in the order covered. |
| `ItemsRemoved` | `long` | Number of entries removed. |
| `BytesFreed` | `long` | Total bytes those entries occupied. |
| `Failures` | `IReadOnlyList<RecycleFailure<EmptyFailureStage>>` | Volumes that could not be emptied or measured. One volume's failures are together, in coverage order. |
| `Elapsed` | `TimeSpan` | Time the call took. |
| `RecycleBinEmptyResult(...)` | constructor | Constructs a result. |

### `EmptyStatus`

The outcome of one entry in an `EmptyAsync` call.

| Value | Meaning |
| --- | --- |
| `Removed` | The entry is out of the Recycle Bin. |
| `Failed` | The entry is still there. `RecycleBinEmptyResult.Failures` carries why. |

`Removed` and `ItemsRemoved` can disagree, and did before this existed: an entry whose size could not be
worked out leaves the bin without being counted.

### `RecycleBinRestoreResult`

The immutable result of one `RestoreAsync` call.

| Member | Type | Description |
| --- | --- | --- |
| `RestoredPaths` | `IReadOnlyList<string>` | Full path of each entry that was restored. |
| `Conflicts` | `IReadOnlyList<RecycleRestoreConflict>` | Collisions encountered, and how each was resolved. |
| `Failures` | `IReadOnlyList<RecycleFailure<RestoreFailureStage>>` | Entries that failed to restore. Empty when `Succeeded` is `true`. |
| `ItemsRestored` | `long` | Number of entries moved out of the Recycle Bin. Never more than the number passed in. |
| `RestoredSize` | `long` | Total size in bytes of the restored entries, from their records. |
| `Elapsed` | `TimeSpan` | Time the call took. |
| `Succeeded` | `bool` | `true` when `Failures` is empty. `Conflicts` does not affect it. |
| `RecycleBinRestoreResult(...)` | constructor | Constructs a result. |

**Guarantees**

| | |
| --- | --- |
| Ordering | Every list keeps the relative order the entries were handed in |
| Indexing | The lists are not an index into the input — one entry of three may appear in none of them |
| A skipped entry | Not a failure. Only an `Error` policy puts it in `Failures` |
| `Conflicts` | Never affects `Succeeded` |
| `RestoredPaths` | Names what was passed in, so a merged directory is one path however many files it settled |
| A file a merge renamed | Named in `Conflicts` only, never in `RestoredPaths` |

**Conflicts from a merge**

| Policy | Records produced |
| --- | --- |
| `MergeSkip` | One for the directory, however many files gave way |
| `MergeReplace`, `MergeRename` | One per file overwritten or renamed |

**Path handling**

| | |
| --- | --- |
| Spelling | `RestoredPaths` keeps the case and trailing separator the entry was recorded with |
| Restored directories | The payload moves to the trimmed name, so check with `Directory.Exists`, not `File.Exists` |
| Renamed entries | Reported under the path they landed at |

> Nothing is rolled back. An entry that was overwritten, or set down under a free name, stays that way,
> and this result is the only record of it.

### `RecycleRestoreConflict`

One path that was already occupied when a restore reached it, and what was done about it. Reported
rather than raised: the caller chose a policy, and the restore carries on with the entries after it.

| Member | Type | Description |
| --- | --- | --- |
| `Path` | `string` | The path the item wanted, or under `RestoredRenamed` and `MergedRenamed` the path it landed at. |
| `OccupiedByDirectory` | `bool` | Whether the occupant is a directory rather than a file. |
| `Action` | `RecycleRestoreConflictAction` | How the conflict was resolved. |
| `RecycleRestoreConflict(...)` | constructor | Constructs a conflict record. |

Under `MergedRenamed` the record names a file inside a merged directory, so it is not in
`RestoredPaths` — that list holds the directory's one path.

#### `RecycleRestoreConflictAction`

How a restore resolved one occupied path.

| Value | Meaning |
| --- | --- |
| `Skipped` | Nothing on disk was touched and the entry is still in the bin. An `Error` policy reports this in `Conflicts` rather than in `Failures`. |
| `RestoredRenamed` | The entry was set down beside the occupied path under the first free name. |
| `MergedSkipped` | A merge left a colliding file alone; the incoming file went back into the bin as an entry of its own. Under `MergeSkip` this is one record for the directory. `Path` is the directory's path. |
| `MergedReplaced` | A merge overwrote a colliding file. One record per file, under `MergeReplace`. `Path` is the path it was overwritten at. |
| `MergedRenamed` | A merge set an incoming file down under a free name of its own, `report.txt` becoming `report (1).txt`. One record per file, under `MergeRename`. `Path` is where the file landed. |

### `RestoreStatus`

The outcome of one entry in a `RestoreAsync` call.

| Value | Meaning |
| --- | --- |
| `Restored` | The entry is back on disk under its own path. |
| `Merged` | The entry was a directory and its contents went into a directory that was already at that path. |
| `Skipped` | The entry is still in the bin because `FileConflict` or `DirectoryConflict` said to leave it there. Not a failure. |
| `Failed` | The entry is still in the bin and the restore did not get it out. `RestoreProgress.Failure` carries why. |

`Skipped` and `Failed` both leave the entry in the bin. `Failed` always reports a `Failure`;
`Skipped` reports one only under an `Error` conflict policy, at stage `RestoreConflictRefused`, and that is
the only way a skipped entry makes `Succeeded` `false`.

### `RestoreFileConflict`

What a restore does with a **file** whose original path is already occupied.

| Value | Effect |
| --- | --- |
| `Skip` | The default. Nothing on disk is touched and the entry stays in the bin; the incoming file gives way, never the one already there. |
| `Replace` | Overwrites the file already there. Never a directory. |
| `Rename` | Sets the entry down beside the occupied path under the first free name. The number starts at one and the extension is kept, splitting on the last dot: `archive.tar.gz` becomes `archive.tar (1).gz`. Nothing on disk is renamed, moved or removed. |
| `Error` | As `Skip`, and the entry is also reported in `Failures`. That is the only difference, and it makes `Succeeded` `false`. |

A destination occupied by the *other* kind is a conflict like any other, and the policy for the kind
arriving is applied: `Replace` onto a directory has nothing to overwrite and is reported at
`RestoreFailed`.

### `RestoreDirectoryConflict`

What a restore does with a **directory** whose original path is already occupied.

| Value | Effect |
| --- | --- |
| `MergeSkip` | The default. The tree's contents go into the directory already there and every file of the same name is left as it is. A file that does not fit goes back into the bin as an entry of its own. The directory is reported once in `Conflicts` as `MergedSkipped`. |
| `MergeReplace` | The same merge, overwriting each file of the same name. A subdirectory of the same name is merged into, not replaced. Each overwritten file is reported as `MergedReplaced`. |
| `MergeRename` | The same merge, setting each colliding file down under a free name of its own. Each is reported as `MergedRenamed`, under the name it landed at. |
| `RenameDirectory` | Sets the whole tree down beside the one already there under a free name, merging nothing. |
| `Skip` | Nothing on disk is touched and the entry stays in the bin — not even missing parent folders are created, so an entry that is not put back leaves nothing behind. |
| `Error` | As `Skip`, and the entry is also reported in `Failures`. That is the only difference, and it makes `Succeeded` `false`. |

No value writes outside the directory you named, and nothing already on disk is renamed or moved.

A destination occupied by a **file** is a conflict like any other: the three `Merge*` values have no tree
to merge into, so the move is refused and reported at `RestoreFailed` with the file untouched, where
`RenameDirectory` takes a free name instead.

A merge can fail after settling every file in its tree. When the payload's own removal, or its record's
deletion, is what the file system refuses, there is no file to name and the entry's own record is the
only one in `Failures`.

### `RecycleBinStatus`

The outcome of a status check. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `VolumeRoot` | `string` | The volume the answer is about. |
| `DriveType` | `DriveType` | What kind of volume it is; `Unknown` when that could not be read. |
| `Reason` | `RecycleBinAvailabilityReason` | Why it is or is not in use. |
| `IsEnabled` | `bool` | `true` only when `Reason` is `Enabled`. |

### `RecycleBinAvailabilityReason`

Why a volume's Recycle Bin is or is not in use. Which value applies decides what a caller can do about it.

| Value | Meaning |
| --- | --- |
| `Enabled` | Nothing disables the Recycle Bin for this volume. |
| `DisabledByPolicy` | A group policy forbids it. Neither the user nor the application can change this. |
| `DisabledByUserPreference` | This volume is set to delete immediately instead of recycling. The user can change it. |
| `UnsupportedVolumeType` | The volume type has no Recycle Bin at all. |
| `VolumeNotFound` | The volume is not mounted, or the path given is not a volume root. |
| `NoRecycleBinOnVolume` | The volume exists and its type supports a Recycle Bin, but no bin was ever recorded for it. No longer produced — an unconfigured volume answers `Indeterminate`. |
| `Indeterminate` | The volume exists but the answer could not be established. |

`Enabled` is the only value that reports a confirmed state. A volume Windows has never configured a bin
for is answered `Indeterminate`, because there is no setting for it either way.

Nothing is cached: a volume mounted, or a policy applied, after one status check is seen by the next.

---

## Examples

### List what is in the Recycle Bin

```csharp
var contents = await RecycleBin.GetItemsAsync();

foreach (var volume in contents.Items)
{
    Console.WriteLine($"{volume.VolumeRoot} — {volume.Items.Count} item(s)");

    foreach (var item in volume.Items)
    {
        Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
    }
}

foreach (var failure in contents.Failures)
{
    Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
}

Console.WriteLine($"{contents.TotalSize} byte(s) in total");
```

### Read every item's details before looping over them

```csharp
var contents = await RecycleBin.GetItemsAsync();

contents.LoadItemDetails();

foreach (var volume in contents.Items)
{
    foreach (var item in volume.Items)
    {
        Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
    }
}
```

### List one volume, reading every record during the listing

```csharp
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

### Empty the Recycle Bin and report what it freed

```csharp
var emptied = await RecycleBin.EmptyAsync(@"W:\");

Console.WriteLine($"freed {emptied.BytesFreed} byte(s) from {emptied.ItemsRemoved} item(s)");

foreach (var failure in emptied.Failures)
{
    Console.Error.WriteLine($"{failure.Path} was not emptied: 0x{failure.HResult:X8}");
}
```

### Restore one item, and report what was in its way

```csharp
var contents = await RecycleBin.GetItemsAsync(@"W:\");

var entry = contents.Items[0].Items[0];

var options = new RestoreOptions { FileConflict = RestoreFileConflict.Rename };

var result = await RecycleBin.RestoreAsync([entry], options);

Console.WriteLine($"{result.ItemsRestored} item(s), {result.RestoredSize} byte(s), in {result.Elapsed.TotalMilliseconds:F0} ms");

foreach (var path in result.RestoredPaths)
{
    Console.WriteLine($"restored: {path}");
}

foreach (var conflict in result.Conflicts)
{
    Console.WriteLine($"conflict: {conflict.Path}, {conflict.Action}");
}

foreach (var failure in result.Failures)
{
    Console.Error.WriteLine($"failed: {failure.Path}, {failure.Stage}, 0x{failure.HResult:X8}");
}
```

### Act on a volume whose Recycle Bin is switched off

```csharp
var status = await RecycleBin.GetStatusAsync(@"W:\");

if (!status.IsEnabled)
{
    Console.Error.WriteLine($"the Recycle Bin is not in use on {status.VolumeRoot}: {status.Reason}");

    if (status.Reason is RecycleBinAvailabilityReason.DisabledByPolicy)
    {
        Console.Error.WriteLine("an administrator set this; nothing in the application can change it");
    }
}
```

### Recycle a batch and report every failure

```csharp
IReadOnlyList<string?> paths =
[
    @"W:\Projects\build-output",
    @"W:\Projects\a-very-long-path\dist",
    @"W:\Projects\gone.log",
];

var result = await RecycleBin.RecycleAsync(paths);

Console.WriteLine($"{result.RecycledPaths.Count} recycled, {result.PermanentlyDeletedPaths.Count} deleted");

foreach (var failure in result.Failures)
{
    Console.Error.WriteLine(
        $"{failure.Path} — {failure.Stage}, 0x{failure.HResult:X8}, attempts={failure.Attempts}");
}
```

### Choose what happens on a volume without a Recycle Bin

```csharp
IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];

var refused = await RecycleBin.RecycleAsync(paths);

foreach (var failure in refused.Failures)
{
    Console.Error.WriteLine($"{failure.Path} was not recycled: {failure.Stage} 0x{failure.HResult:X8}");
}

var options = new RecycleOptions { NoRecycleBinAction = NoRecycleBinAction.DeletePermanently };
var deleted = await RecycleBin.RecycleAsync(paths, options);

Console.WriteLine($"{deleted.PermanentlyDeletedPaths.Count} file(s) deleted, not recycled");
```

### Cancel the wait

```csharp
IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

await RecycleBin.RecycleAsync(paths, cancellationToken: cts.Token);
```

### Recycle one path without throwing

```csharp
var options = new RecycleOptions { ThrowOnError = false };

bool gone = await RecycleBin.RecycleAsync(@"W:\Reports\quarterly.xlsx", options);
```

---

## License

MIT. See [LICENSE](LICENSE).