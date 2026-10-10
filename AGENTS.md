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
volume `--scratch` names, and they empty the Recycle Bin. Two things leave that directory and neither is a
test's doing — the `$RCW-…` staging directories the library creates on the volume root, and the empty
itself. The section below is the whole rule.

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

There are 163 registered cases, of which four are load cases, so `--all` runs 159 of them. The rest is
what the volume decides, not what a flag selects: a run on a volume with no Recycle Bin skips the
cases that recycle one and runs the cases that are about a volume with no bin. Measured on a volume
with a bin: **149 pass and 10 skip**, with nothing failing. A volume with no bin and a share are both
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
