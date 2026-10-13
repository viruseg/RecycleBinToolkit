# Working in this repository

Windows-only, .NET 10, no external NuGet packages beyond `Microsoft.Windows.CsWin32` (a build-time
source generator). The test project is a console application with a hand-rolled harness, not a test
framework.

## Commands

```bash
dotnet build src/RecycleBinToolkit/RecycleBinToolkit/RecycleBinToolkit.csproj
dotnet build tests/RecycleBinToolkit.Tests/RecycleBinToolkit.Tests.csproj
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch X:\ --all
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch X:\ --filter NativeBinSuite
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch X:\ --load
```

Build the test project after any change to `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` —
see the rule below. The suites change machine state: they write into a directory they create inside the
volume `--scratch` names, and they empty the Recycle Bin. One thing leaves that directory and it is not a
test's doing — the empty itself. The section below is the whole rule.

## --scratch, and asking which volume

`--scratch <root>` is required, and every run needs one. It names the **root of a volume** — `X:\`, or
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

Inside the volume root the run claims `X:\{guid}` and takes `scratch` and `long` beneath it. The name is
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

There are 242 registered cases, of which four are load cases, so `--all` runs 238 of them. The rest is
what the volume decides, not what a flag selects: a run on a volume with no Recycle Bin skips the
cases that recycle one and runs the cases that are about a volume with no bin. **226 pass and 12 skip**,
with nothing failing, on a volume that has a bin — measured by one `--all` on `D:\` on 2026-10-13.

Two of the twelve skips are the 8.3 cases: `D:\` has short-name generation off, so `GetShortPathName`
returns nothing for any path there and the alias route has no fixture to run against. What is measured
on `D:\` is the fall-through — an alias that is absent goes to the caller's policy, which
`NativeBinSuite.ThrowRefusesThePathAndLeavesItInPlace` witnesses. What is **not** measured on `D:\` is
the success branch: that a path whose alias fits is recycled with the alias in its record. Enabling
short names needs administrator rights, so nobody should read that route as proven from a run here.

**A volume alone on its physical disk is 198 pass and 15 skip**, also nothing failing. Five cases ask the
machine for a second volume on the same disk or for one on another, through `VolumePresence`, and a run
on such a volume has neither — so each says it did not run rather than going red over a layout it never
chose. This is the whole of what `VolumePresence` is for: a letter written into a case asserts one
machine's partition layout and is green on that machine and red everywhere else, which is the opposite
of what a portable suite owes its caller.

**That second figure is stale and is not re-measured.** It was taken on a two-volume machine before this
work and its case count predates the twelve cases added since, so read it as "how many skip on a volume
with no second volume", not as a number to quote beside the first.

A volume with no bin and a share are both unmeasured
here: measure before quoting either. `--all` prints the counts, so a count in a document that disagrees
with them is the document being wrong.

A run that must not empty the bin says `--exclude EmptySuite`; that drops its eleven cases, so the counts
it prints are eleven lower than a plain `--all` and a document must not quote one run's figures as the
other's.

## How it works

```
RecycleBin            the entry point: moves items in, resolves volumes, reports
   ├─ RecycleBatch           validates, de-duplicates, refuses what cannot be recycled, recycles on the pool
   ├─ RecycleBinVolumeSet   which volumes an operation covers
   ├─ RecycleBinVolumeState  volume table + registry facts
   └─ RecycleBinVolumePolicy the decision, separated so it can be checked exhaustively

the engine, over whatever the entry point resolved
   ├─ IndexFormat             which record version this machine reads, asked once per process
   ├─ NativeBinLayout         $RECYCLE.BIN\<SID>\ and the $I/$R name pair
   ├─ NativeIndexFile         version 1 and 2 records, read and written
   ├─ NativeBinEntries        one walk of the bin, pairing each $I record with its $R payload
   ├─ NativeRecycleRunner     record first, then the move; retries are per path
   ├─ ShortPathResolver       the 8.3 alias, when the volume has one
   ├─ LongPathStager          $RCW-<guid>\ on the item's own volume, for a path the record cannot name
   ├─ NativeBinLister         pairs from the walk, every volume at once; records only under PreloadItemDetails
   └─ NativeBinEmptier        payload first, then the record, counted from the walk; one group of volumes per disk

the restore engine, over the entries a listing handed back
   ├─ RestoreRunner           payload first, then the record; one answer per entry, in the caller's order; one mover pool per volume; a refused move asked again
   └─ RestorePathPlanner      what is in the way of a path, and what to do about the item that wanted it

RecycleBinContents.LoadItemDetails  warms a listing after the fact
  └─ ItemDetailLoader        one volume at a time, at that volume's own degree of parallelism
```

**The record version follows the machine, and the caller has no say.** There are two formats. Version 2
carries a counted path and reads on Windows 10 build 14393 and later; version 1 has a fixed 520-byte
field, which is 259 characters, and reads on everything older. An older Shell does not reject a version 2
record — it reads it as something it cannot use, so the item appears in the bin and cannot be restored,
which is the outcome the write gate existed to prevent. So `IndexFormat` asks the machine once per
process and writes what it can read, and the floor is whatever the platform annotation already said.

**Why the caller cannot choose.** A machine that reads both has nothing to gain from the older one and
long paths to lose; a machine that reads one has no second option at all. A switch would be a setting
whose two settings are right on different machines and wrong on each other's.

**Why the long-path machinery is version 1 only.** A version 2 record holds any path Windows can name, so
there is no path it cannot record and nothing for the policy to decide. Adding the route there would take
a file that works today and move it to a staging directory so the entry would name a path it never lived
at. The whole route is therefore inside `SettleOverLongPaths`, which returns immediately on version 2.

**What a restore's put-back records, and why no policy reaches it.** `RestoreRunner` recycles internally
when a merge cannot place a file. It records the path the entry was deleted from, not the place inside
`$RECYCLE.BIN` where the payload is lying — that is what `RecycleTarget.RecordPath` is for. A restore puts
an item where its record says, and `RestoreOptions` carries no destination of its own. So
`LongPathFailureAction` is on `RecycleOptions` and nowhere else.

**Where that reasoning stops, and what is there instead.** The put-back's path is *not* one a record
already holds. `RestoreRunner.WalkInto` builds it as `Path.Combine(destination, childName)`, where the
destination is the directory entry's own recorded path and the child name is new — so it is longer than
anything in the bin whenever the directory's own recorded path is anywhere near the limit. Under version 1
the alias route is tried first and usually answers; where the volume has no short names, the path is
refused at `LongPathNotRecyclable`, `PutBack` maps that to `RestoreFailed`, and the merge reports the
whole entry failed. Nothing is lost — the payload stays in `$RECYCLE.BIN` — but the restore reports a
failure it would not have reported on version 2, after spending the attempt budget. `RestoreOptions` does
not carry a `LongPathFailureAction` because there is no caller-visible way to make that entry land
somewhere else: the caller's answer to a refused restore is to raise the conflict policy, which stops the
merge before a leftover exists.

**Record first, then the move.** A crash between the two leaves the caller's file exactly where it
was and an index with no payload — litter the next empty collects. The other order leaves data that
is gone and recorded nowhere, which is the one outcome a Recycle Bin exists to prevent.

**Retries are per path, not per batch.** A move that fails is a property of one path, and retrying
the others would repeat work that already succeeded. Only the items that failed are retried.

**The per-volume bin is the unit.** Each volume's `$RECYCLE.BIN\<SID>\` is walked directly, so naming
a volume selects which directories to read and to empty — never a filter over an enumeration that
already happened.

**`GetStatusAsync` answers configuration, but not the future.** Where the registry records a volume's
own setting, that setting is the whole answer. A volume nothing was recorded for answers
`Indeterminate`: unproven, never enabled. That is what makes it trustworthy enough for
`RecycleAsync` to refuse a path rather than destroy it. It still does not predict the next delete:
a volume mounted after the call, or a policy applied after it, changes the answer without the call
having been wrong. Nothing is cached, so each call asks the question again.

The threading arguments — why writing is single-threaded, why reading is not, and why the listing
splits on the volume axis while the restore and the empty split on the disk axis — are in
[Performance, measured](#performance-measured) below rather than repeated here.

## Performance, measured

### Restoring, which is not any of the tables below

A restore is not any of the operations this section otherwise measures, and its figures were taken
separately. It moves files out of the Recycle Bin and deletes their `$I` records — metadata work, not
thirty thousand opens — so the reading figures were not borrowed, and a restore was measured instead.

Over 300 files, median of three, seconds are milliseconds:

| movers per volume | solid-state volume | mechanical volume A | mechanical volume B |
| --- | --- | --- | --- |
| **1** | **250** | **327** | **197** |
| 2 | 408 | 473 | 502 |
| 4 | 1578 | 1678 | 1808 |
| 8 | 1915 | 1784 | 1832 |
| 16 | 1883 | 1642 | 1846 |

A and B are two partitions of one physical disk; the solid-state column is a disk of its own.

**One, and it is not a seek-penalty question.** It falls away monotonically to seven or nine times one
thread, and the curve has the **same shape on the solid-state volume as on both mechanical ones**. A
drive whose seeks cost something and one whose do not would not draw the same curve, so what the threads
are fighting over is not the head. It is the one directory every entry on a volume has in common —
`$RECYCLE.BIN\<SID>\`, where each entry's `$I` record is deleted. That is the same finding this file
records for the writing side, reached from the deleting side, and it is why the answer is one figure for
both kinds of drive rather than two.

Confirmed not to be a small-batch artefact: 2,000 files on the solid-state volume take 1,442 ms on one
mover and 10,703 ms on four — the same ratio.

**Volumes on different physical disks do pay, and it is the one axis that does.** 300 files on the
solid-state volume and 300 on a mechanical one restore in **257 ms**, where the same two volumes one
after another take **458 ms** — 1.78× — because they are different directories. The two mechanical
volumes, sharing one physical disk, take **482 ms**, which is what running them one after another costs
and what `RunDisk`'s loop exists to guarantee. So the rule is not "restore is single-threaded": it is
single-threaded *within* a volume, and concurrent *across* disks.

**Read this before changing anything to do with how many threads the recycle uses.** The
figures below were measured on one machine over batches of 3,000 items, and they are the reason the
code looks the way it does. Parallelising more of it is not an improvement, and on that machine most of
it is the opposite of one.

| operation on a solid-state volume | 1 thread | 2 | 4 | 8 |
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
| mechanical volume, 30,010 records | 1550 ms | 508 | 377 | **351** | 349 |
| solid-state volume, 36,009 records | 1892 ms | 658 | 496 | **457** | 458 |

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
| mechanical volume, 30,010 records | 73.1 s | 23.7 s | 13.6 s | 12.4 s | 4.8 s | **7.7 s** | 9.6 s |
| solid-state volume, 36,009 records | 92.0 s | 43.1 s | 28.1 s | 23.2 s | 18.0 s | **16.1 s** | 16.1 s |

**The mechanical curve peaks at eight and then falls away** — three passes each averaged 7.7 s at eight
against 9.6 s at sixteen, because each further request in flight is another seek for the head to
interleave. Sixteen was in the code before this sweep and came from a warm measurement, where it won by
seven percent; it costs twenty percent on the path that runs every time. `MechanicalReadDegreeCap` is
eight on the cold evidence.

**Removing the one-thread rule is where most of the gain came from**, and it holds cold: one thread on
the mechanical volume is 73 s against 7.7 s at eight, a factor of nine and a half. Measured end to end
against the code as it stood, same session, alternating passes:

| | cold, before → after | warm, before → after |
| --- | --- | --- |
| mechanical volume | 50.7 s → **7.9 s** | 1.650 s → **0.491 s** |
| solid-state volume | 17.1 s → 16.7 s | 0.591 s → 0.548 s |

**A result that is not reproduced here, and is worth knowing:** cold on the mechanical volume comes out
**faster than cold on the solid-state one** — 7.7 s for 30,010 records against 16.1 s for 36,009 — and the
two are read on the same eight threads, so it is not a difference in concurrency. A cold read of a small
record is mostly metadata, and the solid-state volume was the Windows system volume while the mechanical
one is not, so background reads competing with the system volume is the obvious candidate. **That is a
hypothesis and it has not been tested.** Anyone proposing an explanation for it should measure first.

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
  is grouped at all: a caller that asked about `X:\` and found no group for it could not tell an empty bin
  from a volume that was never reached.
- **A single volume skips `Parallel.For` entirely.** One volume has nothing to be parallel with, and the
  caller has already asked for a thread of its own. That is the ordinary case — every listing a caller takes
  of one volume — and nesting a second parallel loop inside the pool thread the caller is already on buys
  nothing.

**Not measured: the gain.** No figure for "a listing of three volumes before → after" exists, on either
volume, and nothing above should be read as implying one. A machine with one volume holding a bin sees no
change at all and is the common case. Anyone quoting a speed-up here is quoting something that was not
measured.

**The listing's axis is the number of volumes, and the restore's and the empty's is the disk.** That is a
difference and not an oversight, so keep it: reading changes nothing, so two partitions of one disk being
walked at once contend for nothing, while moving and deleting are the work that contends for a head. Any
proposal to put the listing on the disk axis, or the other two on the count, is a change to a measured
figure and goes through a measurement first.

**Not measured: the oversubscription.** With `MaxDegreeOfParallelism = volumeRoots.Count` at the volume level
and `DegreeOfParallelism` up to eight inside `Preload`, four volumes asked for with `PreloadItemDetails` on
can put thirty-two threads in flight. That the pool copes is the only evidence there is; it was not measured
under that combination. It is the shape to measure before changing either figure.

**The empty is now split by disk too, and this is where it stopped.** `NativeBinEmptier.Empty` groups
volumes by the physical disk they are partitions of and runs the groups together, the volumes of one disk
one after another, through `RunDisk` — the restore's answer, and a different one from the listing's above.
Each volume writes a slot taken by its position in the argument, so `VolumesEmptied` and `Failures` come
back in the order the caller passed the roots rather than the order the threads finished, which is the only
thing a caller can observe about the order. `VolumeDiskGroups` is the one place the rule lives, shared with
the restore; a second copy of "which volumes share a head" would be free to drift.

**Not measured: the gain.** No figure for "an empty of several volumes before → after" exists, on either
kind of volume, and nothing above should be read as implying one. The argument is that two volumes on two
devices share neither a head nor a directory, so what parallelism removes is one device's wait behind
another's — that is an argument, and it is not a number. Anyone quoting a speed-up here is quoting
something that was not measured, exactly as for the listing above.

**Inside one disk the empty is unchanged**, and that is a decision rather than an omission. `Remove` deletes
payload first and record second, sequentially, over one directory — the same directory that cannot take
concurrent writers, which is why the recycle path is single-threaded. Parallelising it needs its own
measurement, and the nesting the empty now sits inside (one thread per disk, with `DegreeOfParallelism` up
to eight under it for the orphan-`$I` measuring pass) is the same unmeasured combination the listing and
the restore already have.

### What a read actually costs, and what does not

Measured warm over 5,000 records, one thread, on both volumes:

| | mechanical volume | solid-state volume |
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

| mechanical volume, 30,010 records | time |
| --- | --- |
| one `Parallel.For`, 16 threads | **351 ms** |
| channel, 16 readers + 1 parser | 395 ms |
| channel, 8 readers + 2 parsers | 575 ms |
| `BlockingCollection` pipeline, in the library | 598–641 ms |
| overlapped async reads, 16 × 2 | 599 ms |

On the solid-state volume the same comparison was 420 ms for the loop against 426 ms for the channel.
There is nothing to overlap when the parse is two parts in a thousand of the work; the queue and the
extra threads cost more than they save.

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
  on a mechanical volume and 625 on a solid-state one — the cost is filesystem round trips, not bandwidth.
  Four-kilobyte files recycled at 583 and 662, within the run-to-run spread, so the file size is not
  part of the cost either.
- **A recycle costs three round trips per path**: one `FileInfo` query in validation, one
  create-and-write for the record, one move. That is what the optimisation effort went into.

  Every figure above is the median of three runs of a 3,000-file batch. A ratio is the trustworthy figure and an
  absolute one is not.

Do not add parallelism to the recycle path. If a future measurement on some other hardware says
otherwise, that measurement goes here first, and the constant it changed goes in the same commit.

### The name a delete is given, and nothing measured about it

Entry names climb: `$I000000.ext`, `$I000001.ext`, and so on, where before each was six characters
drawn at random. `BinStemSequencer` walks the bin once per volume per operation, puts every stem it
finds into a `HashSet<long>`, and hands out the lowest one not in it. Two consequences are worth writing
down and one is a trap.

The trap is that the set is **not** what keeps a name from being overwritten. `NativeIndexFile.Write`
still creates with `FileMode.CreateNew`, and a refusal carrying a taken name's code is still
`NameCollision.IsTaken`'s to judge — that is unchanged, and a name another process created between the
walk and the create is answered by it exactly as before. Anything that treats the set as the authority is
reintroducing the overwrite this library does not do.

The consequence worth knowing is that **the stem is the key, not the stem with its extension**. A set
keyed on the name minus its marker would let `$I000000.txt` and `$I000000.bin` both exist, hand one
index to two files whose extensions differ, and put each write at a different end of the directory's
index — which is the scattering the order was for.

**Nothing here is measured.** The hypothesis is that writes clustered at one leaf of a directory's index
are cheaper than writes scattered over many, and no figure has been taken for it: not before, not after,
not on either kind of volume. Anyone quoting a speed-up for this is quoting something that was not
measured. The walk itself is a cost the previous code did not pay — one directory listing per volume per
operation, which a delete of a single file into a full bin now carries and a delete of ten thousand at
once would have paid anyway — and it has not been weighed against anything either.

---

## Windows the library has to live with

These are properties of the Recycle Bin rather than choices made here. They are what `README.md`
used to carry under a heading called *Limitations*, which was the wrong home: a caller cannot act on
any of them, and the ones they could act on were already in the API sections.

**A per-volume key that outlives its bin still reads `Enabled`.** Windows records a volume's
Recycle Bin setting when it configures one, and nothing removes that record when the bin later
goes. A key left behind on a volume with no `$RECYCLE.BIN` is therefore treated as evidence that
the volume recycles, because configuration is the evidence throughout. There is no second source to
cross-check it against and no way to ask Windows whether the directory still exists as far as the
answer is concerned.

**The per-volume capacity is not treated as a switch.** A capacity of `0` is not reported as
disabled. The Recycle Bin dialog offers no such setting, so nothing can turn it into one, and no
measurement of what it would do is recorded here — so nothing claims to know.

**Emptying a large bin is file deletion, and it is slow.** Measured: emptying roughly 13,600
accumulated entries took 216 s. There is no progress callback to report from and no way to make it
incremental, which is why a caller that needs responsiveness empties one volume at a time. Volumes
on different physical disks are emptied together, which removes the wait one disk put in front of
another; that is per disk and not per volume, so the partitions of one disk still take their turn.

**A path past what the record can name is a refusal or a move, never a truncation.** This is a
property of the older record format rather than a choice, and it is the one place where the Recycle
Bin's own limits reach a caller. `NativeIndexFile` refuses to write a record whose path will not fit
and writes nothing rather than shortening it, so the caller gets `LongPathNotRecyclable` and their
file where they left it. The path in a record is what a restore puts the item back to: a truncated
one is a wrong answer rather than an approximate one.

---

## What the tests may touch

No file a test creates is ever outside the working directory `--scratch` names. No test acts on a path it
did not derive from there: `--scratch` names the volume, the run claims `X:\{guid}` inside it, and every
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

Three things leave the working directory and are not the tests' doing: `EmptySuite` emptying the
volume's Recycle Bin, which is the only way to assert that an entry really left; the empty itself; and a
staging directory the library creates under the volume root when a long path cannot be recorded. A case
that provokes the third deletes it afterwards, which is cleanup rather than creation — the rule that a
test creates nothing outside `--scratch` is untouched by it, and the directory exists only because the
library needs a short path somewhere on the item's own volume.

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

## How documentation is written here

Three surfaces in this repository are written for a reader: `README.md`, the XML documentation on the
public surface, and comments in the code — `//` and `///` alike. They answer different questions, which is
what the sections below are about. **How the sentences are built is one rule set for all three**, and this
is it.

**A fact is a row, not a paragraph.** A table cell, a bullet, or one sentence. A paragraph that reasons
its way to the fact is the author's thinking, and a reader scanning for the fact has to find it first.
Where a row would need a condition inside the condition — "X, or Y when Z, unless W" — it is two facts:
split it, or give the row the short form and put the detail in a bullet under the table.

**Use the standard word.** Technical writing has an established term for almost everything a library
reports, and the non-standard phrase is nearly always the author's own and nearly always worse:

| Do not write | Write |
| --- | --- |
| did not come back, is back, carried back | failed to restore, restored, total size in bytes |
| removed for good | deleted permanently |
| left cold, not read yet | unread, not loaded |
| Wall-clock time the caller waited | elapsed time |
| asserts a fact, answers for it | reports, returns |
| the path it wanted | the destination path |

If a phrase would not survive being read aloud to a colleague, it does not go in the file.

**Nothing restates what the reader can already see.** Not the line under a comment, not the parameter
type, not the default already in the table above, not the sentence in the paragraph above. A comment
that repeats the code beneath it is worse than no comment: the first read registers nothing and the
reader reads it twice.

**Nothing defends the design.** "Kept for contract stability", "this is the only difference `Error`
makes", "deliberately not `Enabled`" — a reader who did not ask whether the design is defensible is now
being answered a question they did not ask, in a paragraph, before the thing they did ask. State the
behaviour; that is the contract. The justification belongs in this file, and it usually already is.

**One fact, one place.** A fact stated in a table is not repeated in the paragraph under it, in the code
block beside it, or in a second section. Every copy is a place to fall out of date and a place the
reader starts to doubt. Elsewhere, point at it — `see Requirements` — and that is not a repetition.

**One example per distinct thing.** Two examples differing only in the array literal, the drive letter or
one flag are one example. Fifteen of them is fifteen chances to say the same thing more weakly.

**The scannability test.** Read the section as a reader who already knows what they want and wants the
answer, not the argument. If the answer is the third sentence of the third paragraph, it is buried.

## What README.md is for

`README.md` answers three questions: **what this is, why you would reach for it, and what it will do
to you.** A reader arrives from a search for "how do I delete a file to the Recycle Bin in .NET" and
leaves able to write a call. Anything that does not serve that trip does not belong in the file.

**Belongs:**

- Contracts: what a method does, what a parameter means in that call, what comes back, what raises.
- Order and shape guarantees a caller has to code against — which list is ordered by what.
- Limits that are properties of Windows rather than choices made here, because a caller has to know
  them and can do something about them.
- Worked examples.
- The version requirement, stated where a caller looks for it: `Requirements` and the method that
  refuses. **Stated once.** `Requirements` carries the build number and why the floor is where it is;
  the method that refuses links to it rather than repeating the number, and no third section mentions
  it. A fact with one owner is one fact; a fact restated in four places is four places to be wrong.

**Does not belong:**

- How anything is implemented: architecture, components, threads, `$I` and `$R`, `$RECYCLE.BIN`,
  registry keys, SIDs.
- Why an approach was chosen over the alternatives. That is a decision record, and this is not one.
- Measurements, benchmarks and their interpretation. A performance figure is not a contract unless a
  caller changes their code because of it.
- Anything about this repository's tests — what is covered, what is not, how to run the suite.

**The test for a borderline paragraph: could a caller write different code because of this?** If
yes, it is in the README. If the answer is "they would understand the library better", it is here.

**A type section is a table and short rows, not a paragraph under it.** The shape that works is a member
table, then one row per guarantee, then a table per rule that would otherwise run past 300 characters
in a single cell. A guarantee the reader has to reconstruct from four sentences is a guarantee they
will not act on. The same shape for `<returns>`, `<remarks>` and the comment tables: what is true, in
a row.

**Nothing gets deleted to satisfy that.** A fact that leaves the README changes homes, and the three
are:

| Where | What goes there |
| --- | --- |
| `AGENTS.md` | The rationale, the measurement, the architecture, the reasoning |
| A comment on the code | A decision that belongs to one function rather than to the library |
| The test project | Anything about how the behaviour is witnessed |

So removing a README section is a move, not a cut, and a move that cannot find a home is a signal
that the section was answering a question the README was never for.

**Removing a section breaks its anchors.** Every `](#anchor)` pointing into it has to go in the same
change — a dead link in a document is a defect the same as a claim that no longer matches the code.
Check with a grep for the removed heading before finishing.

## README.md and ReadmeExamples.cs are one thing

`README.md` is the documentation. `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` holds the
same examples as compiling methods, which is what keeps a documented API shape from drifting into
something that no longer builds.

**A new example is added to both files in the same change, and a changed example is changed in both.**

- One example, one method, in both places. The method's body is the README code block, character for
  character, and the README sections follow the order of the methods in the file.
- A README block may prepend `using RecycleBinToolkit;` so a reader can paste it as a standalone
  snippet. Nothing else in a block may differ from the method body.
- **A comment that restates the code is deleted.** `var paths = Directory.GetFiles(..., TopDirectoryOnly)`
  needs no note that it collects paths, and `var result = await RecycleBin.RecycleAsync(paths)` needs
  none that it recycles them: a reader reads the English and gets the same. Keep a comment only where
  the line cannot say it — why an unusual overload was chosen, what a value in the block means that is
  not a local name. A comment is copied by whoever pastes the block, so a comment that helps only the
  author is a cost to every reader.
- The file compiles but is never run, so an example may hard-code paths and never touch the disk.
  Give each one a name that says what it demonstrates.
- Nothing else may differ between a block and its method. An example that needs a helper this
  repository happens to have does not belong in the README — the reader does not have it.

When a signature changes, both files change — the tables in README.md, the example in the README, and
the method in `ReadmeExamples.cs`. A README claim that no longer matches the code is a defect the same
as a failing test.

## What the XML documentation is for

**A public member's documentation is read in a tooltip, by someone who has already written the line
above it.** It is a contract and nothing else: what the member does, what each parameter means in
this call, what comes back, what is raised. It is not a second copy of the README and it is not a
decision record.

- **`<summary>` is one sentence.** If the second sentence is a different fact rather than a
  continuation, it usually belongs in `<returns>`, in the README, or nowhere.
- **A default belongs in the summary**, not in a remarks block under it. `Defaults to 6, clamped to at
  least 1` is one line a caller sees without expanding anything; a `/// <remarks>The default is 6.
  /// </remarks>` is two tags they have to expand to read.
- **`<param>` describes that parameter's role in that member.** It does not restate the parameter
  type, and it does not re-describe `RecycleBinOptions` on all seven methods of `RecycleBin`. The
  boilerplate "Retry, verification and error-handling settings. `null` means
  `RecycleBinOptions.Default`. `ThrowOnError` decides whether a failure raises." is a description of
  a type and belongs on the type. Where a parameter has nothing to add to its own signature, omit
  the tag entirely rather than writing something that restates the declaration.
- **`<returns>` says what the caller gets**, including the empty and sentinel cases: whether a list
  is empty, whether `null` means "none", what a count is measured from rather than computed after.
- **`<exception>` documents only the intended contract.** Say what state the member left the caller
  in — the file is still where it was — rather than narrating the path that led there. **One row per
  cause.** A single `<exception>` tag carrying "the move failed, or the volume refused it, or the
  action was X, and only when `ThrowOnError` is on" is four rows the reader has to separate by hand,
  and one of them will be read wrong.
- **`<remarks>` survives only for a caveat the caller needs at the call site**, and only if it fits in
  a sentence or two. A method whose contract needs an essay is a method whose essay belongs in the
  README. Where the remark enumerates independent facts — what a skip does, what an error does, what a
  merge does — it is a bullet list, not a paragraph that reasons from one to the next.
- **Device structure stays out.** `$I`, `$R`, `$RECYCLE.BIN`, the SID, `NukeOnDelete`, `FILE_ID_INFO`
  and the version 2 record layout are how the bin is built, not what a caller does. The one exception
  is a fact a caller would trip over — that a restored path may carry a trailing separator, so
  `File.Exists` answers false of a directory — and that is stated as the trap, not as the layout.
- **The version requirement is stated once, in the README's `Requirements`.** A `<remarks>` that
  repeats the build number puts it in a tooltip the reader cannot act on and creates a second copy to
  fall out of date. Link to `Requirements`; do not restate it.
- **Never reference a test.** No `<summary>`, `<remarks>`, `<param>`, `<returns>` or `<exception>`
  anywhere in `src/` may mention a suite, a case, a test run or this repository's own coverage. The
  dependency is one-directional: tests reference the library, the library never references the tests.
  A coverage claim has no home in the library's own documentation — put it in the test that would change
  if it stopped being true. Check with a grep before finishing.

**Internal and private members keep their comments.** They are not shown to a caller, they are the
right home for a note to the next reader, and a `remarks` on one of them costs nothing. The rules
above are about the surface a caller sees, not about the code's readability.

## Comments in the code

A `//` comment is read by whoever maintains the function next, which makes it the one place a *why*
belongs. The rules from [How documentation is written here](#how-documentation-is-written-here) apply
to it like anywhere else — the comment does not restate the line under it, and a phrase that would not
survive being read aloud does not go in the file — and these three are the rest.

| Kind | Belongs where | Example |
| --- | --- | --- |
| A business rule or domain invariant | A comment on the line that enforces it | Why this field is written before the move rather than after |
| A workaround for a platform or a bug | A comment on the workaround, naming what it works around | Why `RtlGetVersion` rather than `Environment.OSVersion` |
| A decision one function made | A comment on that function, in two or three lines | Why this runs sequentially: the measured numbers |
| A decision the whole library made | `AGENTS.md`, not a comment | Why writing is single-threaded |
| A measurement | `AGENTS.md`, with the sweep it came from | — |

**A comment that explains the code is worse than none**, because it is read once and trusted, and it
goes stale the moment the code changes while the comment does not. Prefer a name that says it: if a
comment is explaining what a well-named local does, the name is wrong.

**A comment may be longer than its neighbours.** A function whose behaviour is not obvious from its
signature — a retry loop with a budget, a state machine, a walk that pairs two enumerations — earns a
paragraph. What it does not earn is a second paragraph about why the author chose it, unless that
reason is not visible anywhere else.

## Claims

Behaviour stated in README.md should be traceable to a measurement in
`docs/superpowers/specs/2026-10-06-recycle-bin-reliability-design.md`, and new behaviour should arrive
with a test that was seen to fail first.
