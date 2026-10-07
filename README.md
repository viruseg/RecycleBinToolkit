# RecycleBinToolkit

Move files and directories to the Windows Recycle Bin from .NET, with per-item diagnostics that
tell you *why* an item failed.

```csharp
using RecycleBinToolkit;

await FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip");
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
using RecycleBinToolkit;

var paths = Directory.GetFiles(@"C:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

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
var options = new RecycleBinOptions { ThrowOnError = false };

var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);
if (!result.Succeeded)
{
    // Every failure is in result.Failures, with an HRESULT and a stage.
    LogWarning(result.Failures);
}
```

---

## API

### Methods

| Method | Returns | Description |
| --- | --- | --- |
| `FileRecycler.MoveToRecycleBinAsync(string? filePath, RecycleBinOptions? options = null, CancellationToken cancellationToken = default)` | `Task<bool>` | Recycles one file or directory. `true` when it is gone from disk. |
| `FileRecycler.MoveToRecycleBinAsync(IReadOnlyList<string?> filePaths, RecycleBinOptions? options = null, CancellationToken cancellationToken = default)` | `Task<RecycleBinResult>` | Recycles a batch and reports the outcome of every item. |

Both are safe to call from any thread, including one that already runs on a single-threaded
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

## Types

### `FileRecycler`

The entry point. A static class with the two methods above. All Shell work runs on a dedicated
worker thread, so a caller never pays for COM initialisation and never blocks inside the Shell.

### `RecycleBinOptions`

Immutable; set with `with`-style initialisers. Every property has a production default, so
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
| `MaxShellPathLength` | `int` | `260` | Longest path the Shell can be asked to recycle. Measured: 251 recycles, 261 does not. Clamped to at least 1. |

```csharp
var options = new RecycleBinOptions
{
    MaxAttempts = 10,
    InitialRetryDelay = TimeSpan.FromMilliseconds(100),
    OverallTimeout = TimeSpan.FromSeconds(5),
    ThrowOnError = false,
    LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
};
```

### `RecycleBinResult`

The outcome of a batch. Immutable.

| Member | Type | Description |
| --- | --- | --- |
| `Succeeded` | `bool` | `true` when every requested path is gone. |
| `RecycledPaths` | `IReadOnlyList<string>` | Paths, as you supplied them, confirmed recycled. |
| `PermanentlyDeletedPaths` | `IReadOnlyList<string>` | Paths removed permanently instead of recycled, because you opted in. Empty unless that happened. |
| `Failures` | `IReadOnlyList<RecycleFailure>` | Paths that were not handled. Empty when `Succeeded`. |
| `Elapsed` | `TimeSpan` | Wall-clock time, including retries. |
| `LeftoverStagingDirectory` | `string?` | A staging directory that still holds an item, or `null`. See [Limitations](#limitations). |
| `FirstFailure` | `RecycleFailure?` | The first recorded failure, or `null`. |
| `Create(...)` | `static RecycleBinResult` | Constructs a result. Useful in tests, rarely in application code. |

> `Succeeded` means **gone from disk**, not necessarily recycled. A file removed permanently under an
> explicit policy counts as success. Check `PermanentlyDeletedPaths` when that distinction matters.

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
bool shellSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
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

### `LongPathFailureAction`

What to do with a path past the Shell's limit.

| Value | Effect |
| --- | --- |
| `Throw` | Default. The file is left untouched and reported as `LongPathNotRecyclable`. Raises `PathTooLongException` only when `ThrowOnError` is also on. |
| `DeletePermanently` | The file is removed for good and listed in `PermanentlyDeletedPaths`. |
| `ShortenPathAndRecycle` | The file is renamed to a short path on its own volume and recycled from there. |

`ThrowOnError` is the global switch and has the final say. With it off, `LongPathFailureAction.Throw`
raises nothing and the path arrives in `Failures` instead.

---

## Examples

### Recycle a directory tree

The single-path overload returns a `bool`. To inspect the reason for a failure, use the batch
overload with one element:

```csharp
IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
var result = await FileRecycler.MoveToRecycleBinAsync(paths);
Console.WriteLine(result.Succeeded ? "recycled" : $"{result.Failures.Count} failed");
```

### Report every failure with its reason

```csharp
foreach (var failure in result.Failures)
{
    Console.Error.WriteLine(
        $"{failure.Path} — {failure.Stage}, hresult=0x{failure.HResult:X8}, attempts={failure.Attempts}");
}
```

### Get long paths into the Recycle Bin

The Shell cannot recycle a path past `MAX_PATH`. This policy moves the item to a short path on the
same volume — a rename, so it costs nothing regardless of size — and recycles it from there.

```csharp
var options = new RecycleBinOptions
{
    LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
};

var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);
```

> The Recycle Bin entry shows the staging path as the original location, not where the file lived.
> That is inherent: the Shell records the path it was asked to delete.

### Never let a file be destroyed without a decision

```csharp
var options = new RecycleBinOptions
{
    LongPathFailureAction = LongPathFailureAction.DeletePermanently
};

var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

if (result.PermanentlyDeletedPaths.Count > 0)
{
    Console.Error.WriteLine($"{result.PermanentlyDeletedPaths.Count} file(s) were deleted, not recycled.");
}
```

### A service that must not throw

```csharp
var options = new RecycleBinOptions { ThrowOnError = false };

var result = await FileRecycler.MoveToRecycleBinAsync(paths, options, stoppingToken);

if (!result.Succeeded)
{
    foreach (var failure in result.Failures)
    {
        logger.LogWarning("recycle failed for {Path}: {Stage} 0x{HResult:X8}", failure.Path, failure.Stage, failure.HResult);
    }
}
```

### Cancel the wait

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await FileRecycler.MoveToRecycleBinAsync(paths, cancellationToken: cts.Token);
```

### Assert in a test

```csharp
var result = await FileRecycler.MoveToRecycleBinAsync(paths);

Assert.True(result.Succeeded);
Assert.Empty(result.Failures);
Assert.All(result.RecycledPaths, path => Assert.False(File.Exists(path)));
```

---

## How it works

```
FileRecycler          validates, normalises, de-duplicates, dispatches
  └─ RecycleBinWorker one STA thread per process, warmed up, reused
       └─ RecycleOperationRunner   attempt loop, selective retry, receipt check
            ├─ ShellOperationContext   COM apartment + one reused IFileOperation
            ├─ RecycleProgressSink     IFileOperationProgressSink -> hrDelete per item
            ├─ RecycleBinReceipt       did it land in $RECYCLE.BIN?
            └─ LongPathStager          short path on the same volume
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
  recognisable. Its presence is reported in `LeftoverStagingDirectory` when the process lives long
  enough to notice.

---

## Testing

```bash
dotnet run --project tests/RecycleBinToolkit.Tests -- --all
dotnet run --project tests/RecycleBinToolkit.Tests -- --filter LongPathSuite
dotnet run --project tests/RecycleBinToolkit.Tests -- --filter RecycleBinVolumeTracksCount
```

`--filter` matches any part of a case name, and every case is named `<Suite>.<Case>`. It exits
non-zero when a case fails.

The suite is a console application with no external packages: 52 cases covering batch and single-path
recycling, per-item failure reporting, selective retry against a self-inflicted transient lock,
concurrent and STA callers, directories, 10 MB files, paths past `MAX_PATH` under all three policies,
and a ten-cycle stress run asserting 100 % Recycle Bin delivery with an independent
`SHQueryRecycleBin` byte-count check. It writes only to `E:\RC\Test` (or the project root) and moves
the committed fixture files it uses.

The harness must cope with the Windows indexer: it creates and recycles thousands of files in one
directory, so handles appear and disappear underneath it. Cleanup and staging both retry for that
reason.

## Contributing

The project has a specification at
`docs/superpowers/specs/2026-10-06-recycle-bin-reliability-design.md` that records the measurements
the design rests on. Behaviour claimed here should be traceable to a measurement there, and new
behaviour should arrive with a test that was seen to fail first.

## License

No license has been chosen yet. Choose one before publishing — until then the code is unlicensed and
not usable outside this repository.
