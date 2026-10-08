# RecycleBinToolkit

Move files and directories to the Windows Recycle Bin from .NET, with per-item diagnostics that
tell you *why* an item failed — and read, empty, and inspect the Recycle Bin itself.

```csharp
using RecycleBinToolkit;

await FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip");

await RecycleBin.GetItemsAsync();
await RecycleBin.EmptyAsync(@"E:\");
```

- **Targets** `net10.0`, Windows only. The library is `AnyCPU`; the test project is `x64`.
- **Dependency**: `Microsoft.Windows.CsWin32` only, and it is a build-time source generator.

---

## What it is

`IFileOperation` — the Shell API behind the Recycle Bin — has no way to report *which* item in a
batch failed, or *why*. With UI suppression enabled, which any unattended application must set, a
failed delete is skipped silently and the operation still returns success. Code written against the
raw API therefore has exactly three options: guess from `File.Exists`, retry the whole batch, or
delete without the Recycle Bin.

This library wraps that API so a caller gets the answer instead of the guess:

| Problem with the raw Shell API | What this library does |
| --- | --- |
| Per-item failures are invisible | A progress sink captures `hrDelete` for every item |
| One locked file silently skips the rest | Only genuinely failed items are retried; the rest succeed on the first attempt |
| A single bad path aborts the whole queue | Path problems are reported per item and never abort a batch |
| A path past `MAX_PATH` is deleted **permanently** while reporting success | Detected before the delete happens; the policy is yours to choose |
| A delete that lands outside the Recycle Bin is reported as success | Rejected, and reported as a failure with its stage |
| Creating an STA thread and a COM apartment per call dominates batched work | One warmed-up worker thread per process, reused |
| `Marshal.ReleaseComObject` on a live RCW risks a double `IUnknown::Release` | RCWs are left to the garbage collector |
| `SHEmptyRecycleBin` reports success without saying what it removed | Counted before and after, per volume, and the totals are measured |
| Explorer column indexes for Recycle Bin details shift with locale and Windows build | Documented property keys and Shell names, which do not |

## Why it exists

Because of one measured behaviour. On the machine this library was developed against:

```
path length   Shell reports   Recycle Bin gain   outcome
     71-251   success         +1 item, full size recycled
        261   success         +0 items, +0 bytes deleted permanently
```

Past `MAX_PATH` the Shell removes the file for good, returns `0x00270008` — a success code — and
reports no destination. Trusting `hrDelete` alone loses the file silently. Detecting this is
post-hoc, by which point the data is gone, so this library refuses to hand such a path to the Shell
in the first place.

---

## Requirements

| | |
| --- | --- |
| OS | Windows 7 or later (the Shell COM interfaces need it) |
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

await FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip");
```

A batch, reporting failures as data:

```csharp
// TopDirectoryOnly on purpose: a whole tree is one item, and the batch overload is for many
// paths the caller already has.
var paths = Directory.GetFiles(@"C:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

// This overload never throws for a per-item failure — a bad path lands in result.Failures
// and the rest of the batch still runs.
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

if (!result.Succeeded)
{
    foreach (var failure in result.Failures)
    {
        // Attempts is 0 when the item never reached the Shell, so the HRESULT is 0 too.
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
| `RecycleBin.GetItemsAsync(string? volumeRoot = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinContents>` | Lists what is in the Recycle Bin. |
| `RecycleBin.EmptyAsync(string? volumeRoot = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinEmptyResult>` | Empties it, and reports how much it freed. |
| `RecycleBin.GetStatusAsync(string volumeRoot, CancellationToken cancellationToken = default)` | `Task<RecycleBinStatus>` | Whether it is in use for a volume, and why not if it is not. |

All five are safe to call from any thread, including one that already runs on a single-threaded
apartment.

### `MoveToRecycleBinAsync(string?, RecycleBinOptions?, CancellationToken)`

Recycles a single file or directory.

| Parameter | Type | Description |
| --- | --- | --- |
| `filePath` | `string?` | Path of the file or directory. |
| `options` | `RecycleBinOptions?` | Retry, verification and error-handling settings. `null` means `RecycleBinOptions.Default`. |
| `cancellationToken` | `CancellationToken` | Stops the wait for the Shell. |

**Returns** `true` when the path is gone from disk, whether it was recycled or removed permanently
under an explicit policy.

**Exceptions**

| Exception | When |
| --- | --- |
| `ArgumentException` | `filePath` is null, empty or whitespace and `ThrowOnError` is on |
| `FileNotFoundException` | `filePath` does not exist and `ThrowOnError` is on |
| `PathTooLongException` | The path cannot be recycled, no short name is available, and `LongPathFailureAction` is `Throw` |
| `IOException` | The Shell refused the delete and `ThrowOnError` is on. `HResult` carries the Shell error code |
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

**Exceptions** `ArgumentNullException` when `filePaths` is null, and `OperationCanceledException`
when the token is cancelled. Per-item problems are data, not exceptions.

---

## The Recycle Bin itself

`RecycleBin` is a second entry point next to `FileRecycler`. Where `FileRecycler` moves items *in*,
`RecycleBin` reads, empties, and reports on the bin as a whole.

All three of its methods are batch-shaped: they never throw for an environmental problem. A volume
that is not mounted or an entry the Shell will not describe is data in the result, because a service
that lists or empties drives must survive one unmounted letter.

`GetItemsAsync` and `EmptyAsync` share the one worker thread with `FileRecycler`, so every COM call
in the process stays on one apartment and no caller pays for COM initialisation.
`GetStatusAsync` needs no COM at all and never queues behind them.

### `GetItemsAsync`

Lists the Recycle Bin.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to keep, such as `"E:\"`, or `null` for every volume. |
| `cancellationToken` | `CancellationToken` | Cancels the wait. |

**Returns** a `RecycleBinContents`.

The Recycle Bin is a single Shell folder spanning every mounted volume, so `volumeRoot` narrows an
enumeration that has already happened. It is a filter, not a cheaper query.

### `EmptyAsync`

Empties the Recycle Bin. Confirmation, progress window and sound are always suppressed: an unattended
library must never put a dialog on a caller's desktop.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string?` | Volume root to empty, or `null` for every volume, in which case every mounted Fixed or Removable volume is covered in a single Shell call. A path inside a volume is not a volume root: it is reported in `Failures` rather than emptied as that whole volume. |
| `cancellationToken` | `CancellationToken` | Cancels the wait. An empty already handed to the Shell always finishes. |

**Returns** a `RecycleBinEmptyResult`.

`SHEmptyRecycleBin` reports success whether it removed ten thousand entries or none, and says nothing
about either count. Each volume is therefore queried before and after with `SHQueryRecycleBin`, and
`ItemsRemoved` and `BytesFreed` are the measured differences. Neither is ever negative: a Recycle Bin
that grew mid-run is reported as zero rather than as a negative removal.

A volume counts as failed only when the Shell refused **and** something is demonstrably still in it.
The Shell reports an error when asked to empty a Recycle Bin that is already empty, so trusting the
HRESULT alone would fail a call that did exactly what was asked.

### `GetStatusAsync`

Whether the Recycle Bin is in use for one volume.

| Parameter | Type | Description |
| --- | --- | --- |
| `volumeRoot` | `string` | Volume root to inspect. Must be a volume root, not a directory inside one. |
| `cancellationToken` | `CancellationToken` | Checked before the work starts. |

**Returns** a `RecycleBinStatus`. **Throws** `ArgumentException` for a null or blank path.

Windows exposes no authoritative query for this. What happens on the next delete is decided by a group
policy, by a per-volume setting the user owns, and by the kind of volume, so an answer that is only
"off" throws away the one thing a caller can act on.

---

## Types

### `FileRecycler`

The entry point. A static class with the two methods above. All Shell work runs on a dedicated
worker thread, so a caller never pays for COM initialisation and never blocks inside the Shell.

### `RecycleBinOptions`

Immutable; set with object initialisers. Every property has a production default, so
`RecycleBinOptions.Default` is always a valid argument.

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Default` | `RecycleBinOptions` | — | Shared instance carrying the defaults. |
| `MaxAttempts` | `int` | `6` | Attempts per item before it is reported as failed. Clamped to at least 1. |
| `InitialRetryDelay` | `TimeSpan` | `50ms` | Delay before the first retry; it doubles each attempt, with up to 25 % jitter. Negative values clamp to zero. |
| `OverallTimeout` | `TimeSpan` | `30s` | Upper bound on wall-clock time spent retrying the batch. A backoff that does not fit the remaining budget ends the loop immediately rather than starting an over-budget round. |
| `VerifyRecycleBinReceipt` | `bool` | `true` | Reject a delete whose reported destination is outside `$RECYCLE.BIN`. See [Limitations](#limitations). |
| `ThrowOnError` | `bool` | `true` | Whether a failure raises instead of being returned. The global switch; it has the final say over `LongPathFailureAction`. |
| `LongPathFailureAction` | `LongPathFailureAction` | `Throw` | What happens to a file the Shell cannot recycle. |
| `NoRecycleBinAction` | `NoRecycleBinAction` | `Throw` | What happens to a file whose volume has no Recycle Bin to put it in. |
| `MaxShellPathLength` | `int` | `260` | Longest path the Shell can be asked to recycle. Measured: 251 recycles, 261 does not. Clamped to at least 1. |

```csharp
// Every property has a production default, so RecycleBinOptions.Default — which is what a null
// options argument means — is always a valid argument.
var options = new RecycleBinOptions
{
    MaxAttempts = 10,
    InitialRetryDelay = TimeSpan.FromMilliseconds(100),
    OverallTimeout = TimeSpan.FromSeconds(5),
    ThrowOnError = false,
    LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
};

// ThrowOnError is the global switch and has the final say over LongPathFailureAction.
// 251 recycles and 261 does not, measured on the machine this was developed against.
Console.WriteLine(options.MaxShellPathLength);
```

### `RecycleBinResult`

The outcome of a batch. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Succeeded` | `bool` | `true` when every requested path is gone. |
| `RecycledPaths` | `IReadOnlyList<string>` | Paths, as you supplied them, confirmed recycled. |
| `PermanentlyDeletedPaths` | `IReadOnlyList<string>` | Paths removed permanently instead of recycled, because you opted in. Empty unless that happened. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Paths that were not handled. Empty when `Succeeded`. |
| `Elapsed` | `TimeSpan` | Wall-clock time the caller waited, including any queueing behind other Shell work. |
| `LeftoverStagingDirectory` | `string?` | The first staging directory that still holds an item, or `null`. See [Limitations](#limitations). |
| `LeftoverStagingDirectories` | `IReadOnlyList<string>` | Every staging directory still holding an item. A batch spanning volumes can leave more than one, and each holds your data. Empty when nothing was left. |
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
| `HResult` | `int` | Shell error code, or `0` when the Shell was never reached. |
| `Attempts` | `int` | Attempts made. `0` means the item never reached the Shell. |
| `Stage` | `RecycleFailureStage` | Where in the pipeline it failed. |

**Reading `HResult`.** Success and failure are told apart by the severity bit, not by comparing with
zero: the Shell reports a successful delete as `0x00270008`, never as `S_OK`.

```csharp
var failure = new RecycleFailure(@"C:\x.bin", unchecked((int)0x80270027), 1, RecycleFailureStage.Performing);

// The Shell reports a successful delete as 0x00270008, never as S_OK, so success and failure
// are told apart by the severity bit rather than by comparing with zero. On its own that is
// not enough: an item can carry a success code and still be a failure, which is why Stage
// is read too.
bool shellSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
Console.WriteLine(shellSucceeded);
```

A locked file reports `0x80270027` at stage `Performing`.

### `RecycleFailureStage`

| Value | Meaning |
| --- | --- |
| `PathValidation` | The path was empty, malformed, or pointed at nothing. |
| `ShellItemCreation` | The Shell refused to produce an item for the path. |
| `Queueing` | The item could not be added to the pending operation. |
| `Performing` | The Shell performed the delete and reported a failure for this item. |
| `ReceiptVerification` | The delete reported success but the result did not reach the Recycle Bin. |
| `LongPathNotRecyclable` | The path is too long for the Shell. It was never handed over. |
| `PermanentDeleteFailed` | The path was too long and removing it outright was asked for, but the file system refused — a handle is held, the attributes are read-only. The file is exactly as the caller left it. |
| `Enumeration` | The Shell would not describe a Recycle Bin entry, or a volume could not be read. Only reachable while reading or emptying the Recycle Bin. |

`HResult` carries the Shell's own code, or the file system's where the file system refused. Test
`(HResult & 0x80000000) != 0` to tell a refusal from a Shell success code such as `0x00270008`. That is
not sufficient on its own: an item can carry a success code and still be a failure, which is what
`ReceiptVerification` is. A rejected destination is reported with the Shell's success code, so read
`Stage` as well.

### `LongPathFailureAction`

What to do with a path past the Shell's limit.

| Value | Effect |
| --- | --- |
| `Throw` | Default. The file is left untouched and reported as `LongPathNotRecyclable`. Raises `PathTooLongException` only when `ThrowOnError` is also on. |
| `DeletePermanently` | The file is removed for good and listed in `PermanentlyDeletedPaths`. |
| `ShortenPathAndRecycle` | The file is renamed to a short path on its own volume and recycled from there. |

`ThrowOnError` is the global switch and has the final say. With it off, `LongPathFailureAction.Throw`
raises nothing and the path arrives in `Failures` instead.

### `NoRecycleBinAction`

What to do with a path on a volume that cannot recycle it.

| Value | Effect |
| --- | --- |
| `Throw` | Default. The path is refused, the file stays where it is, and the Shell is never asked. Raises only when `ThrowOnError` is also on. |
| `AllowIndeterminate` | The path is handed over even though the volume's answer was not `Enabled` — the answer was `Indeterminate` or `NoRecycleBinOnVolume`. The Shell may remove the file for good and still report success. |
| `DeletePermanently` | The file is removed for good and listed in `PermanentlyDeletedPaths`, never in `RecycledPaths`. |

The values run from safest to most destructive, and `ThrowOnError` is still the global switch: with it
off, a refusal arrives in `Failures` instead of an exception.

### `RecycleBinItem`

One entry in the Recycle Bin. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Name` | `string` | File or directory name, as it was before deletion. |
| `OriginalPath` | `string` | Full path the entry was deleted from. |
| `RecycledPath` | `string` | Physical path of the renamed `$R` entry, inside the Recycle Bin. The name cannot be reconstructed from the other fields, and this is what a restore or a targeted delete needs. |
| `Size` | `long` | Size in bytes. `0` for a directory — see [Limitations](#limitations). |
| `DeletedAt` | `DateTimeOffset` | When it was deleted, as recorded by the Shell, presented in the local offset. |
| `IsDirectory` | `bool` | Whether the entry is a directory. |

**Where each field comes from was measured, not assumed.** The obvious sources come back empty on a
current Windows build, which is why this is worth stating:

| Field | Source | What does *not* work |
| --- | --- | --- |
| `OriginalPath`, `Name` | `SIGDN_NORMALDISPLAY`, which the Shell fills with the full original path. `System.Recycle.DeletedFrom` holds only the directory, and is the fallback when the name is not rooted | `System.ItemNameDisplay` comes back empty |
| `RecycledPath` | `SIGDN_FILESYSPATH` | — |
| `DeletedAt` | `System.Recycle.DateDeleted` through `IShellItem2` | — |
| `Size` | The entry's own file on disk | `System.Size` returns `0` for everything in the Recycle Bin |
| `IsDirectory` | `SFGAO_FOLDER` | — |

Binding an `IPropertyStore` onto a Recycle Bin entry answers `CO_E_NOT_SUPPORTED`, which is why the
recycle properties are read through `IShellItem2` instead.

> One original path can appear more than once. Deleting the same file twice leaves two entries with two
> deletion times, and both are listed.

### `RecycleBinContents`

The outcome of a listing. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Items` | `IReadOnlyList<RecycleBinItem>` | The entries that were read. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Entries the Shell would not describe, at stage `Enumeration`. |
| `Elapsed` | `TimeSpan` | Wall-clock time spent enumerating. |
| `TotalSize` | `long` | Sum of `Size` over `Items`, derived when the listing was built. |

### `RecycleBinEmptyResult`

The outcome of an empty. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `VolumesEmptied` | `IReadOnlyList<string>` | Volume roots covered, each with a trailing separator. |
| `ItemsRemoved` | `long` | Entries that left the Recycle Bin. Measured, never negative. |
| `BytesFreed` | `long` | Bytes they occupied. Measured, never negative. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Volumes that could not be emptied or measured. |
| `Elapsed` | `TimeSpan` | Wall-clock time the caller waited, including any queueing behind other Shell work. |

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
| `NoRecycleBinOnVolume` | The volume exists and its type supports a Recycle Bin, but the Shell reports that it has none. |
| `Indeterminate` | The volume exists but the answer could not be established. Deliberately not `Enabled`. |

`Enabled` is the only value that asserts a fact, and this library does not assert what it could not
read. The per-volume setting is keyed on the volume's GUID, and Windows writes that key only for
volumes it has configured a bin for, so an absent key means "this volume was never configured", not
"this volume recycles". Only a volume whose own key reads `NukeOnDelete=0` is answered `Enabled` from
configuration; anything else is asked of the Shell. A Shell that names no Recycle Bin at all is
`NoRecycleBinOnVolume` rather than `Indeterminate`: measured here, `SHQueryRecycleBin` answered
`0x80004005` on a removable volume with no `$RECYCLE.BIN` and `0x80070003` on a network share, while
an empty bin queried normally.

---

## Examples

Every example below is compiled as the body of a method in
`tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs`, so what is shown here is what was built
against. They are in the order they appear in that file.

### List what is in the Recycle Bin

```csharp
var contents = await RecycleBin.GetItemsAsync();

foreach (var item in contents.Items)
{
    // Size is 0 for a directory — its contents cannot be enumerated without elevation, so
    // IsDirectory is the honest signal that no figure is available.
    Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
}

// TotalSize is derived from Items when the listing is built, so it cannot disagree with them.
Console.WriteLine($"{contents.Items.Count} item(s), {contents.TotalSize} byte(s)");
```

### List one volume, and hear about anything unreadable

```csharp
// The Recycle Bin is one Shell folder spanning every mounted volume, so this filters an
// enumeration that has already happened rather than being a cheaper query.
var onE = await RecycleBin.GetItemsAsync(@"E:\");

foreach (var failure in onE.Failures)
{
    Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
}
```

### Empty the Recycle Bin and report what it freed

```csharp
// Confirmation, progress window and sound are always suppressed: an unattended library must
// never put a dialog on a caller's desktop.
var emptied = await RecycleBin.EmptyAsync(@"E:\");

// SHEmptyRecycleBin says nothing about how much it removed, so both figures are measured per
// volume before and after. Neither is ever negative.
Console.WriteLine($"freed {emptied.BytesFreed} byte(s) from {emptied.ItemsRemoved} item(s)");

foreach (var failure in emptied.Failures)
{
    Console.Error.WriteLine($"{failure.Path} was not emptied: 0x{failure.HResult:X8}");
}
```

### Act on a volume whose Recycle Bin is switched off

```csharp
// A volume root, not a directory inside one.
var status = await RecycleBin.GetStatusAsync(@"E:\");

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
IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

Console.WriteLine(result.Succeeded ? "recycled" : $"{result.Failures.Count} failed");
```

### Report every failure with its reason

```csharp
// A path that is not there comes back as a failure and does not stop the other entries.
IReadOnlyList<string?> paths = [@"C:\Projects\build-output", @"C:\Projects\gone.log"];
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

foreach (var failure in result.Failures)
{
    Console.Error.WriteLine(
        $"{failure.Path} — {failure.Stage}, hresult=0x{failure.HResult:X8}, attempts={failure.Attempts}");
}
```

### Get long paths into the Recycle Bin

```csharp
IReadOnlyList<string?> paths = [@"C:\Projects\a-very-long-path\build-output"];
var options = new RecycleBinOptions
{
    LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
};

// Past MAX_PATH the Shell deletes the file for good and reports a success code, so the path is
// never handed over. This policy renames the item to a short path on its own volume — a rename,
// so it costs nothing regardless of size — and recycles it from there.
var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

// The entry shows the staging path as the original location, not where the file lived. That is
// inherent: the Shell records the path it was asked to delete.
//
// A directory left here still holds an item that could neither be recycled nor moved back. The
// item is never deleted in that state, so it is recoverable — the caller has to move it out.
// A batch spanning volumes can leave more than one, so read the list when that is possible.
foreach (var directory in result.LeftoverStagingDirectories)
{
    Console.Error.WriteLine($"still holding your data: {directory}");
}
```

### Never let a file be destroyed without a decision

```csharp
IReadOnlyList<string?> paths = [@"C:\Projects\a-very-long-path\archive.zip"];
var options = new RecycleBinOptions
{
    LongPathFailureAction = LongPathFailureAction.DeletePermanently
};

var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

// Only files past MAX_PATH are affected; everything else still goes to the Recycle Bin.
if (result.PermanentlyDeletedPaths.Count > 0)
{
    Console.Error.WriteLine($"{result.PermanentlyDeletedPaths.Count} file(s) were deleted, not recycled.");
}
```

### Cancel the wait

```csharp
IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

// Cancelling stops the wait, not the work: a round already handed to the Shell runs to
// completion, so a cancelled call may still recycle its files.
await FileRecycler.MoveToRecycleBinAsync(paths, cancellationToken: cts.Token);
```

### Refuse a batch on a volume without a Recycle Bin

```csharp
IReadOnlyList<string?> paths = [@"F:\Reports\quarterly.xlsx"];
var options = new RecycleBinOptions { ThrowOnError = false };

// The default NoRecycleBinAction is Throw: on a volume with no Recycle Bin the Shell removes the
// file for good and still reports success, so the path is refused before it ever reaches the
// Shell. With ThrowOnError off the refusal comes back as data instead of an exception.
var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

foreach (var failure in result.Failures)
{
    Console.Error.WriteLine($"{failure.Path} was not recycled: {failure.Stage} 0x{failure.HResult:X8}");
}
```

### Assert in a test

`Check.*` here is the test project's own assertion helper; in a project with xunit the same three
lines read `Assert.True`, `Assert.Empty` and `Assert.All`.

```csharp
IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
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

## How it works

```
FileRecycler          validates, normalises, de-duplicates, dispatches
  └─ RecycleBinWorker one STA thread per process, warmed up, reused
       ├─ RecycleOperationRunner   attempt loop, selective retry, receipt check
       │    ├─ ShellOperationContext   COM apartment + one reused IFileOperation
       │    ├─ RecycleProgressSink     IFileOperationProgressSink -> hrDelete per item
       │    ├─ RecycleBinReceipt       did it land in $RECYCLE.BIN?
       │    └─ LongPathStager          short path on the same volume
       ├─ RecycleBinLister          virtual Recycle Bin folder -> per-entry properties
       └─ RecycleBinCounter         SHQueryRecycleBin, for before-and-after measurement

RecycleBin            validates, filters, reports
  ├─ RecycleBinVolumeState  volume table + registry facts
  └─ RecycleBinVolumePolicy the decision, separated so it can be checked exhaustively
```

**One round, one answer per item.** Each round builds Shell items for the unfinished targets, queues
them against a progress sink keyed on exactly those paths, and performs once. An item counts as
recycled when the Shell cleared the failure bit on `hrDelete` **and** the file is gone from disk.
Only the items that failed are re-queued.

**Why a dedicated thread.** Shell operations need a single-threaded apartment, and every COM object
involved must be created and used on one thread. Creating a thread per call makes thread startup and
COM initialisation the dominant cost when files are deleted in a loop, so the worker is created once,
warmed before the first request, and reused. Calls are dispatched there even when the caller is
already on an STA thread, which keeps `CoInitializeEx` out of an incompatible apartment mode.

**Paths exist in two forms.** `SHCreateItemFromParsingName` rejects the extended-length `\\?\` prefix
with `E_INVALIDARG`, while the .NET file APIs require it. Paths are therefore kept both ways: the
prefixed form for `File` and `Directory`, the plain form for the Shell.

**Listing is one apartment, not one thread per caller.** Enumerating the Recycle Bin needs the same
apartment as deleting from it, so it queues on the same worker. The cost of that choice is that a
listing of a large Recycle Bin waits behind an in-flight batch rather than running beside it.

**The Recycle Bin is reached by CLSID, not by path.** `C:\$Recycle.Bin` is the per-volume storage:
enumerating it directly yields one folder per user SID instead of the deleted items, with the SYSTEM
entry among them denying access to an unelevated process. The virtual folder aggregates every volume,
which is also why filtering by volume is a filter and not a cheaper query.

**Measured cost of a listing.** 523 entries took 337 ms, and 23 entries took 150 ms, so roughly 120 ms
is the fixed cost of opening the aggregate folder and 0.4–0.6 ms is per entry. The per-entry cost is
the Shell's: six COM round trips, of which reading the deletion time is only about 15%. The work this
library adds per entry is one file-system stat. `GetStatusAsync`, which needs no COM, measures 0.06 ms
a call.

---

## Limitations

Stated plainly, because these are properties of Windows rather than choices.

- **A path past `MAX_PATH` cannot be recycled by the Shell.** The Shell deletes it permanently and
  reports success. This library refuses to hand such a path over by default; `ShortenPathAndRecycle`
  works around it at the cost of the original location shown in the Recycle Bin.
- **A full Recycle Bin can also delete permanently while reporting success.** The Shell reports no
  destination in that case, and a missing destination cannot be told apart from a successful delete
  that simply did not report one. `VerifyRecycleBinReceipt` can reject a destination *outside* the
  Recycle Bin, but it cannot prove the positive case.
- **Cancellation stops the wait, not the work.** A round already handed to the Shell runs to
  completion, so a cancelled call may still recycle its files.
- **Temporary locks are retried, not eliminated.** An indexer, preview handler or antivirus holding a
  file open is transient; the retry policy absorbs it. A lock held for the whole run fails the item,
  and only that item.
- **8.3 short names are not available everywhere.** They are the first rescue for a long path, and
  short-name generation is a per-volume setting. Where it is disabled, only the policies above apply.
- **The Shell is Windows-only.** There is no cross-platform fallback.
- **A staging directory can survive a crash.** It is named `$RCW-<32 hex>` so a leftover is
  recognisable. Its presence is reported in `LeftoverStagingDirectories` when the process lives long
  enough to notice.
- **A recycled directory reports `Size` as zero.** Its contents cannot be enumerated without
  elevation, and the Shell reports those as zero too, so there is no figure to give. `IsDirectory` is
  the honest signal that a size is not available.
- **A listing covers every mounted volume, always.** Filtering by volume happens after enumeration
  because the Recycle Bin is a single aggregate folder. A caller who wants only `E:\` still pays for
  the volumes it does not care about, and it cannot see a volume that is not mounted.
- **`GetStatusAsync` reports configuration, not behaviour.** It answers whether the Recycle Bin is
  configured for a volume, not whether the next delete will land there. A volume mounted after the
  call, or a policy applied after it, changes the answer without the call having been wrong. It is also
  not consulted by anything else in this library: `FileRecycler` reports what the Shell actually did.
- **The per-volume Recycle Bin capacity is not treated as a switch.** A capacity of `0` is not
  reported as disabled, because the Recycle Bin dialog offers no such setting and no measurement of its
  behaviour is recorded here.
- **Emptying a large Recycle Bin is slow, and that is the Shell deleting files.** Measured: emptying
  roughly 13,600 accumulated entries took 216 s. There is no progress callback to report from and no
  way to make it incremental; a caller that needs responsiveness should empty one volume at a time.

---

## Testing

```bash
dotnet run --project tests/RecycleBinToolkit.Tests -- --all
dotnet run --project tests/RecycleBinToolkit.Tests -- --filter LongPathSuite
dotnet run --project tests/RecycleBinToolkit.Tests -- --filter RecycleBinListingAgreesWithTheShellOracle
dotnet run --project tests/RecycleBinToolkit.Tests -- --load
```

`--filter` matches any part of a case name, and every case is named `<Suite>.<Case>`. It exits
non-zero when a case fails.

The suite is a console application with no external packages: 94 cases covering batch and single-path
recycling, per-item failure reporting, selective retry against a self-inflicted transient lock,
concurrent and STA callers, directories, 10 MB files, paths past `MAX_PATH` under all three policies,
and a ten-cycle stress run asserting 100 % Recycle Bin delivery. Listing, emptying and status checking
are covered by their own suites. It writes only to `E:\RC\Test` (or the project root) and moves the
committed fixture files it uses.

**Four of the 94 are load cases and run only on request.** They are the ones that work the disk hard enough
to notice: the ten-cycle stress run, and the three cases that each move the whole 94-file fixture set
through the Shell in one call. `--all` and `--filter` never run them — `--load` runs those four cases and
nothing else. Nothing is covered by them alone; the claims sit in ordinary cases too —
`WorkflowSuite.FiveTouchedFilesAreAllRecycled` and `EmptySuite.EmptyReportsWhatItFreed` for the batch,
`FacadeSuite.LargeFileIsRecycled` for the bytes.

**The Recycle Bin suites empty it.** `EmptySuite` removes what the earlier suites recycled, and it is
the only way to assert that an entry really left. Everything else only ever adds to it.

**One test keeps the measurement honest.** Every suite reads the Recycle Bin through
`RecycleBinProbe`, which uses the library's own listing API. That would prove only that the library
agrees with itself, so `ShellSuite.RecycleBinListingAgreesWithTheShellOracle` asserts the same thing
against `SHQueryRecycleBin` — a different call into the Shell that shares no code with the enumeration
— on both sides of one known delete. On this machine the two report identical figures, which is what
makes the rest of the delta assertions mean anything. The agreement is asserted in one place rather
than in every suite on purpose: forty checks against the same API are still forty checks against the
same API.

The harness must cope with the Windows indexer: it creates and recycles thousands of files in one
directory, so handles appear and disappear underneath it. Cleanup and staging both retry for that
reason.

Two things about the harness are worth knowing before reading its timings. Reading a count asks
`SHQueryRecycleBin` rather than listing the bin, because a listing costs six COM round trips and a stat
per entry while a count question needs none of it — on a bin holding ninety thousand files that is the
difference between about eleven seconds and over thirteen minutes. The count is not free, though: the
Shell walks the bin's metadata, so it still costs roughly a tenth of a second per thousand files, and
the suites read a handful of times per test. What they assert about the listing itself lives in
`ListerSuite`, which pays for it deliberately.

`EmptySuite` empties the volume it runs against. It is the one suite that changes machine state rather
than only reading it, and on a Recycle Bin holding tens of thousands of entries it will take tens of
minutes, because emptying that many files is what the Shell does.

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
