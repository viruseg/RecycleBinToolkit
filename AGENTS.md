# Working in this repository

Windows-only, .NET 10, no external NuGet packages beyond `Microsoft.Windows.CsWin32` (a build-time
source generator). The test project is a console application with a hand-rolled harness, not a test
framework.

## Commands

```bash
dotnet build src/RecycleBinToolkit/RecycleBinToolkit/RecycleBinToolkit.csproj
dotnet build tests/RecycleBinToolkit.Tests/RecycleBinToolkit.Tests.csproj
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --all
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --filter NativeBinSuite
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --load
```

Build the test project after any change to `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` —
see the rule below. The suites change machine state: they write into a directory they create inside the
volume `--scratch` names, and they empty the Recycle Bin. One thing leaves that directory and it is not a
test's doing — the empty itself. The section below is the whole rule.

## --scratch, and asking which volume

`--scratch <root>` is required, and every run needs one. It names the **root of a volume** — `D:\`, or
`\\server\share\`, or the forward-slash spelling `//server/share` for a command line that cannot carry two
leading backslashes. A directory inside a volume is refused, as is a bare drive letter: both resolve
against something other than the volume they name. Without the flag the runner runs nothing and exits 2,
because the suite writes files and deletes directories and neither is worth guessing at.

**Any volume is a legal answer.** `--scratch` does not require a volume that recycles. A run on a volume
with no Recycle Bin runs the cases that are about such a volume and skips the ones that are not, so it is
green-or-skip and never red for want of the right kind of disk. A removable drive is a first-class choice
here, not a fallback.

**A share has to survive the command line, and the documented spelling does not everywhere.**
`--scratch "\\server\share\"` arrives intact in PowerShell and `cmd.exe`, which treat backslashes literally.
Anything that reads `\\` as an escape delivers `\server\share\` instead, whose `Path.GetPathRoot` is `\` —
a path on the *current drive*, so the directory the cases create lands wherever the process happens to be.
That has already cost this repository one. `--scratch "//server/share/"` is the spelling to use from such
a shell: the forward-slash form reaches the runner intact, and `VolumeRoot.Normalize` converts it, because
the library deliberately refuses it (`RecycleBinVolumeState.RootForm` only reduces backslashes) and the
conversion belongs where the command line is read. A root that is not a volume root at all is refused by
`VolumeRoot.Normalize`, before anything is created on it.

**Ask which volume to test on. Do not pick one.** The flag has no default and the repository's own location
is not one either. A run whose disk was chosen for it put its writes and its recursive deletes somewhere
the caller did not say, which is the one thing this flag exists to prevent — so the question is worth
asking even when the answer looks obvious, and it is asked before the run, not after it fails.

Inside the volume root the run claims `D:\{guid}` and takes `scratch` and `long` beneath it. The name is
generated and checked against what is already there, because `Directory.CreateDirectory` reports success
for a directory somebody else already had — a fixed name would let a case write into it and then delete it.
Removal is of the claimed path only, at the end of the run. No test path is derived from the repository,
so no test writes inside the working copy.

## Test groups

The harness has two disjoint groups. `--all` and `--filter <substring>` select the regular group,
`--load` selects the load group, and no argument other than `--load` runs a load case. A `--filter` that
names only load cases fails with a message naming them and telling the reader to pass `--load`, so the
flag cannot be worked around by accident. A `--backend` on the command line is refused with exit 2: the
flag chose which path a run measured, and there is one path now.

`--exclude <substring>` (or `-x`) takes the cases whose name contains the substring back out of the run,
which is how a run says it must not empty the Recycle Bin: `--all --exclude EmptySuite`. It narrows the
group the run selected, so it composes with `--filter`, and it needs `--all`, `--filter` or `--load` to
do that — on its own it is refused with exit 2 rather than quietly rewriting the default run. It is read
under the rule `--filter` is read under, so `--exclude --load` is a mistake rather than a substring.

There are 200 registered cases, of which four are load cases, so `--all` runs 196 of them. The rest is
what the volume decides, not what a flag selects: a run on a volume with no Recycle Bin skips the
cases that recycle one and runs the cases that are about a volume with no bin. Measured on `D:`, which
has a bin: **186 pass and 10 skip**, with nothing failing. A volume with no bin and a share are both
unmeasured here: measure before quoting either. `--all` prints the counts, so a count in a document
that disagrees with them is the document being wrong.

## Performance, measured

**Read this before changing anything to do with how many threads the recycle uses.** The
figures below were measured on this machine over batches of 3,000 items, and they are the reason the
code looks the way it does. Parallelising more of it is not an improvement, and on this machine most of
it is the opposite of one.

| operation on `C:` (solid-state) | 1 thread | 2 | 4 | 8 |
| --- | --- | --- | --- | --- |
| read 3,000 records back | 291 ms | 140 ms | 96 ms | **64 ms** |
| empty 3,000 entries | 1310 ms | 1248 ms | 1145 ms | **998 ms** |
| recycle 3,000 files of 64 KiB | **542 files/s** | 402 | 200 | 189 files/s |

Reading and emptying behave as the seek-penalty rule in `VolumeSeekPenalty` says. **Recycling behaves
the other way round, and no amount of threading fixes it.** A Recycle Bin is one directory per volume —
`<volume>$RECYCLE.BIN\<SID>\` — and a directory is the one thing a file system cannot take concurrent
writers on. Every record written and every payload moved lands in that same directory, so eight threads
contend on it and finish two and a half times slower than one. `NativeRecycleRunner` is therefore
single-threaded on purpose, and a comment in it says so with these numbers.

The threading table was taken on the code as it stood at that sweep; the throughput figures below were
taken after the allocation work that followed. The table's claim is the shape — reading gains, recycling
loses, both on a solid-state volume — and that shape is a property of a directory rather than of this
library's speed. Read the two tables as different moments rather than as one run.

### Reading a full bin: the degree of parallelism is per drive type

The table above is a solid-state volume. **A mechanical volume reads the same way, and that is the
whole of the change** — it used to get one thread, and it now gets up to eight.

| read N records back, warm cache | 1 thread | 4 | 8 | 16 | 32 |
| --- | --- | --- | --- | --- | --- |
| `E:` mechanical, 30,010 records | 1550 ms | 508 | 377 | **351** | 349 |
| `C:` solid-state, 36,009 records | 1892 ms | 658 | 496 | **457** | 458 |

`VolumeSeekPenalty.DegreeOfParallelism` answers one thread per core for a solid-state volume and two per
core for a mechanical one, both capped at eight. A share is still one thread.

**The reason a mechanical volume was single-threaded was about writing, and it does not transfer.**
A head asked for four hundred files at once thrashes between them — but that is a property of one
directory taking concurrent writers, which is what `NativeRecycleRunner` does and still does on
purpose. A *read* of a Recycle Bin has thirty thousand independent opens of files nobody is writing to.

### The cold path decides the cap, and it is not the warm one

A user's first read of a listing is cold — the cache is empty — so the cold curve is the one that
matters. Measured with the cache dropped before every pass and ten seconds of settling after it:

| threads | 1 | 2 | 3 | 4 | 6 | 8 | 16 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `E:` mechanical, 30,010 records | 73.1 s | 23.7 s | 13.6 s | 12.4 s | 4.8 s | **7.7 s** | 9.6 s |
| `C:` solid-state, 36,009 records | 92.0 s | 43.1 s | 28.1 s | 23.2 s | 18.0 s | **16.1 s** | 16.1 s |

**The mechanical curve peaks at eight and then falls away** — three passes each averaged 7.7 s at eight
against 9.6 s at sixteen, because each further request in flight is another seek for the head to
interleave. Sixteen was in the code before this sweep and came from a warm measurement, where it won by
seven percent; it costs twenty percent on the path that runs every time. `MechanicalReadDegreeCap` is
eight on the cold evidence.

**Removing the one-thread rule is where most of the gain came from**, and it holds cold: one thread on
`E:` is 73 s against 7.7 s at eight, a factor of nine and a half. Measured end to end against the code as
it stood, same session, alternating passes:

| | cold, before → after | warm, before → after |
| --- | --- | --- |
| `E:` mechanical | 50.7 s → **7.9 s** | 1.650 s → **0.491 s** |
| `C:` solid-state | 17.1 s → 16.7 s | 0.591 s → 0.548 s |

**A result that is not reproduced here, and is worth knowing:** cold on the mechanical volume comes out
**faster than cold on the solid-state one** — 7.7 s for 30,010 records against 16.1 s for 36,009 — and the
two are read on the same eight threads, so it is not a difference in concurrency. A cold read of a small
record is mostly metadata, and `C:` is the Windows system volume while `E:` is not, so background reads
competing for `C:` is the obvious candidate. **That is a hypothesis and it has not been tested.** Anyone
proposing an explanation for it should measure first.

**Do not read these as a licence to make the recycle path parallel.** Writing a bin and reading one are
different work and the tables above are why they get different answers.

### Listing several volumes: the volumes are read together

A listing walks each volume's bin directory, and a Recycle Bin is one directory per volume — so two volumes
are two directories on two devices, and nothing about them contends. One disk waiting on its head does not
hold up another, so `NativeBinLister.TryEnumerate` reads every volume at once, one thread per volume. That is
a different axis from everything measured above: the tables are about *how many threads one volume's records
are read on*, and this is about *how many volumes are read at the same time*. `VolumeSeekPenalty.
DegreeOfParallelism` does not answer it and is not consulted for it, because the listing's per-volume work is
one `FileSystemEnumerable` walk of a flat directory — the same walk however the disk underneath answers.

Three things about it are settled by what the code has to preserve, and two are not measured at all.

Settled:

- **The order of the answer does not follow the threads.** Each volume writes into a slot taken by its
  position in the list handed in, and the slots are flattened in that order once every walk is done. Two
  listings of the same machine are comparable and `Items[i]` is the volume that was asked about at `i`.
- **A volume read with nothing in it keeps its group**, holding an empty list. This is also why the answer
  is grouped at all: a caller that asked about `E:\` and found no group for it could not tell an empty bin
  from a volume that was never reached.
- **A single volume skips `Parallel.For` entirely.** One volume has nothing to be parallel with, and the
  caller has already asked for a thread of its own. That is the ordinary case — every listing a caller takes
  of one volume — and nesting a second parallel loop inside the pool thread the caller is already on buys
  nothing.

**Not measured: the gain.** No figure for "a listing of three volumes before → after" exists, on either
volume, and nothing above should be read as implying one. A machine with one volume holding a bin sees no
change at all and is the common case. Anyone quoting a speed-up here is quoting something that was not
measured.

**Not measured: the oversubscription.** With `MaxDegreeOfParallelism = volumeRoots.Count` at the volume level
and `DegreeOfParallelism` up to eight inside `Preload`, four volumes asked for with `PreloadItemDetails` on
can put thirty-two threads in flight. That the pool copes is the only evidence there is; it was not measured
under that combination. It is the shape to measure before changing either figure.

**Do not extend this to the empty.** `NativeBinEmptier` is still sequential across volumes, and unlike a
listing, an empty deletes: running several over one machine is a different question with different answers
about interleaving and about what a partial failure means. Changing it is its own piece of work with its own
measurements.

### What a read actually costs, and what does not

Measured warm over 5,000 records, one thread, on both volumes:

| | `E:` mechanical | `C:` solid-state |
| --- | --- | --- |
| `File.Exists` (metadata only) | 26 µs | 26 µs |
| open and close | 32 µs | 32 µs |
| open, read, close | **47 µs** | **47 µs |
| parse the path and the time | 0.10 µs | 0.10 µs |

Two things follow, and both were measured rather than reasoned into.

**A warm read is identical on a solid-state and a mechanical volume.** The cache has the bytes, so the
cost is what the operating system charges per file and not bandwidth at all. Anything proposed on the
theory that a fast disk needs less concurrency is answering the wrong question.

**The parse is 0.2 % of the work.** A read is three system calls; the rest is arithmetic and one string
allocation. This is what makes the next section a list of things not to do.

### Reading is not worth a pipeline

Splitting the reads from the parsing — across a `System.Threading.Channels` channel, and across a
`BlockingCollection` pipeline over dedicated threads — was built and measured on both volumes, and
**lost to one `Parallel.For` loop in every configuration**:

| `E:` mechanical, 30,010 records | time |
| --- | --- |
| one `Parallel.For`, 16 threads | **351 ms** |
| channel, 16 readers + 1 parser | 395 ms |
| channel, 8 readers + 2 parsers | 575 ms |
| `BlockingCollection` pipeline, in the library | 598–641 ms |
| overlapped async reads, 16 × 2 | 599 ms |

On `C:` the same comparison was 420 ms for the loop against 426 ms for the channel. There is nothing to
overlap when the parse is two parts in a thousand of the work; the queue and the extra threads cost more
than they save.

**A caveat that produced a wrong number once.** An earlier sweep appeared to show the channel winning by
8 %, and it was an artefact: the channel variant used a `CreateFileW` P/Invoke while the loop used
`File.OpenHandle`. Comparing two shapes means both must do the same I/O. It also produced a
`GetAwaiter().GetResult()` in a synchronous public API, which is sync-over-async and does not belong
there. The two mistakes were found by measuring the pipeline inside the library rather than beside it.

**Overlapped reads are worse than synchronous ones** on both volumes (599 vs 351 ms, and 563 vs 467 ms).
A 92-byte read from cache is dominated by the cost of the open, and an overlapped open has to hand the
work to the completion port and wait to be told it finished.

Two measurements that are worth not re-deriving:

- **The same operation is faster on an SSD by about nothing.** Recycling 3,000 files ran at 616 files/s
  on `E:` (mechanical) and 625 on `C:` (solid-state) — the cost is filesystem round trips, not bandwidth.
  Four-kilobyte files recycled at 583 and 662, within the run-to-run spread, so the file size is not
  part of the cost either.
- **A recycle costs three round trips per path**: one `FileInfo` query in validation, one
  create-and-write for the record, one move. That is what the optimisation effort went into.

  Every figure above is the median of three runs of a 3,000-file batch. A ratio is the trustworthy figure and an
  absolute one is not.

Do not add parallelism to the recycle path. If a future measurement on some other hardware says
otherwise, that measurement goes here first, and the constant it changed goes in the same commit.

---

## What the tests may touch

No file a test creates is ever outside the working directory `--scratch` names. No test acts on a path it
did not derive from there: `--scratch` names the volume, the run claims `D:\{guid}` inside it, and every
case writes beneath that. A drive letter or a share written into a case is data for a path predicate —
`NormalizeVolumeRoot(@"W:\")` decides what a string means and touches nothing — and no case creates,
recycles, empties or deletes anything on such a path. `ScratchDirectory.Claim(parent)` refuses any parent
that is not under the claimed root, and it refuses after resolving the path, so `..` cannot escape and a
forward-slash spelling is taken rather than turned away.

Where a case needs a volume that is not there, it asks the operating system for a letter nothing is
mounted on — `AbsentVolume.Letter()` — and re-checks that it is still absent immediately before the call.
Where a case needs a volume that has no Recycle Bin, it asks the library what the volume it was given is —
`ScratchWorkspace.Reason` — and skips when the answer is the other kind, in the shape
`RequireNonRecycling(because)` and `RequireRecycling(because)` hold between them. A test does not test
what is not on the disk.

A third one is worth knowing about, because it looks like a leftover and is not:
`NoRecycleBinAction.AllowIndeterminate` hands a path to the engine on a volume the library reports no
bin for, and the engine answers that by creating the per-user directory under `$RECYCLE.BIN`
and recycling into it. So running on a volume that has no Recycle Bin **can leave one there**.
`FacadeSuite.NoRecycleBinOptionDecidesWhetherThePathIsRecycled` is the case that does it — it is the
only one, and it empties the directory and removes it again in a `finally`, and only when it was the
case that made it. A run on a bin-less volume that leaves a `$RECYCLE.BIN` behind has not found a bug;
it has found a run of a suite where that case did not finish.

Two things leave the working directory and are not the tests' doing: `EmptySuite` emptying the
volume's Recycle Bin, which is the only way to assert that an entry really left.

### A known limit

`EmptySuite.cs` records that `GetStatusAsync` and `EmptyAsync` are supposed to read a *share* root the
same way, and that no case makes that comparison any more: a local volume cannot witness it, because
`NormalizeVolumeRoot` and `Path.GetPathRoot` already agree on a drive-letter root, and the suite does not
reach for the network to get a share. `AForwardSlashSpellingIsTheVolumeToEmpty` is the guard on a local
drive and `AnExtendedLengthRootIsTheVolumeToEmpty` is its sibling for the prefixed spelling; both ask for
a bin to be there first, so both skip on a bin-less volume — which is exactly the hardware the removed share
case existed for. That makes this a limit, not a claim with coverage behind it. Writing the case is the
fix, and it needs a volume that is not on the disk, which is the one thing this rule exists to keep out of
the source.

## Load tests

**A load case is the one that puts real load on the CPU or the disk, and nothing else qualifies.** Volume,
repetition, or a bound measured in elapsed time. The number of files it touches is not the test: ten files
of ten bytes are not a load case, and neither is a single 10 MB file, and neither is a batch of five small
files recycled in one call. Ask what the machine is doing — `StressSuite` regenerates a 94-file fixture set
ten times and moves 204 files, which is where the disk gets worked hard enough to notice.
A batch over the whole fixture set is a load case on its own; the fixture set is 94 files of 64 KiB, so
creating and moving all of them is 5.75 MiB of writing for one call that still finishes in about a second.

**Mark the case where it is declared**, as the third argument of `new TestCase(...)`:
`new TestCase("Suite." + nameof(Case), Case, TestKind.Load)`. A load case stays in the suite that owns the
behaviour; the grouping belongs to the runner, not to the directory layout.

**A load case is never the only coverage of a claim.** Before marking one, confirm an ordinary case already
asserts the same thing without the load, and write that ordinary case if none does. The split exists so the
default run still proves the library works; a claim only `--load` checks is a claim the default run does not
make. What is allowed to stay behind the flag is the repetition itself — "this holds across ten cycles" is
what a load case is for, and no ordinary case can restate it.

The four load cases are all covered this way, and the ordinary cases that carry the claims are the reason
they are allowed to sit behind a flag:

| Load case | Covered without the load by |
| --- | --- |
| `StressSuite.TenCopyRecycleCyclesAlwaysSucceed` | `WorkflowSuite.FiveTouchedFilesAreAllRecycled`, `EmptySuite.EmptyReportsWhatItFreed` |
| `StressSuite.RecycledBytesReachTheBin` | `FacadeSuite.LargeFileIsRecycled` |
| `FacadeSuite.BatchOfGeneratedFilesIsRecycled` | `WorkflowSuite.FiveTouchedFilesAreAllRecycled`, `EmptySuite.EmptyReportsWhatItFreed` |
| `WorkflowSuite.ReadPartialAndAppendThenRecycle` | `WorkflowSuite.FiveTouchedFilesAreAllRecycled` |

## README.md and ReadmeExamples.cs are one thing

`README.md` is the documentation. `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` holds the
same examples as compiling methods, which is what keeps a documented API shape from drifting into
something that no longer builds.

**A new example is added to both files in the same change, and a changed example is changed in both.**

- One example, one method, in both places. The method's body is the README code block, character for
  character, and the README sections follow the order of the methods in the file.
- A README block may prepend `using RecycleBinToolkit;` so a reader can paste it as a standalone
  snippet. Nothing else in a block may differ from the method body.
- Comments belong inside the code block, not only in the surrounding prose. A reader who copies the
  block gets the comment with it. Keep them to what the method names cannot say.
- The file compiles but is never run, so an example may hard-code paths and never touch the disk.
  Give each one a name that says what it demonstrates.
- The one sanctioned difference is spelled in **both** files rather than kept between them: the assertion
  example reads `Check.*` in `README.md` and in `ReadmeExamples.cs`, and both carry the note that in a
  project with xunit the same three lines read `Assert.True`, `Assert.Equal` and `Assert.False`. Never
  "fix" one file to `Assert.*` to match a rule that says the README spells it that way — it does not.

When a signature changes, both files change — the tables in README.md, the example in the README, and
the method in `ReadmeExamples.cs`. A README claim that no longer matches the code is a defect the same
as a failing test.

## Claims

Behaviour stated in README.md should be traceable to a measurement in
`docs/superpowers/specs/2026-10-06-recycle-bin-reliability-design.md`, and new behaviour should arrive
with a test that was seen to fail first.
