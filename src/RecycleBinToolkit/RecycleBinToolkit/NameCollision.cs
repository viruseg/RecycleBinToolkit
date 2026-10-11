namespace RecycleBinToolkit;

/// <summary>
/// Whether a refusal is a name this library drew being taken, which is the one refusal worth trying again
/// under a different name. Read by the two paths that draw a name of their own,
/// <see cref="NativeRecycleRunner"/> and <see cref="RestoreRunner"/>.
/// </summary>
/// <remarks>
/// <b>One judgment, because two of them is how this one was wrong twice.</b> Each of those classes had its
/// own copy of the comparison, they had drifted apart in the type they took, and the masking landed on the
/// restore path first and in the recycle runner afterwards — a case was written for the recycle runner and
/// none for the other. They share this now, so there is a second copy to find.
/// <para>
/// <b>The comparison is witnessed from both call paths; the branch either of them takes is not.</b>
/// <c>NativeBinSuite</c> and <c>RestoreSuite</c> each stage the refusals their own move raises and read them
/// back through here. What no run can arrange is the collision that reaches the branch: neither
/// <see cref="NativeBinLayout.NextStem"/> nor <see cref="RestorePathPlanner.WithFreeNumber"/> takes a name to
/// collide on, so making either call site land on a taken name would take a hook inside the library rather than
/// an arrangement in a test.
/// </para>
/// </remarks>
internal static class NameCollision
{
    /// <summary>Win32 <c>ERROR_FILE_EXISTS</c>, raised by a create that would overwrite a name.</summary>
    private const int ErrorFileExists = 80;

    /// <summary>Win32 <c>ERROR_ALREADY_EXISTS</c>, raised by a move onto a name that is already there.</summary>
    private const int ErrorAlreadyExists = 183;

    /// <summary>
    /// The bits of an HRESULT that carry the Win32 error number, which is where a code the file system raised
    /// can be compared against the two above.
    /// </summary>
    private const int ErrorMask = 0xFFFF;

    /// <summary>
    /// Whether a refusal was a name being taken rather than the item being unavailable.
    /// </summary>
    /// <param name="failure">
    /// What the create or the move raised. Any <see cref="Exception"/>: an exception of a type the file
    /// system does not raise is answered <see langword="false"/> rather than trusted, because a code set by
    /// something else means nothing here.
    /// </param>
    /// <returns>
    /// <see langword="true"/> only for an <see cref="IOException"/> whose code is one of the two above. It
    /// answers rather than raises, because it is read inside exception filters, where a raise would replace
    /// the refusal it was asked about.
    /// </returns>
    /// <remarks>
    /// Telling the two apart is what lets a collision be retried on a fresh name without a locked file being
    /// charged for a name it never lost. A locked file and a source that is not there are facts about the
    /// caller's own data, and the caller's own budget is what asks them again; a taken name is a fact about
    /// this library's own generator and is its own business.
    /// <para>
    /// <b>The code is the low half of the HRESULT and not the whole of it.</b> <c>HRESULT_FROM_WIN32(e)</c> is
    /// <c>0x80070000 | e</c>, so a refusal carries the severity bit and the <c>ERROR_</c> facility on top of
    /// the bare errno: measured on this machine, a create over a name that is taken arrives as
    /// <c>0x80070050</c> and a move onto one as <c>0x800700B7</c>. Comparing the whole value against those two
    /// constants is therefore never true, so no collision was ever recognised — the recycle runner redrew
    /// names it had drawn itself, and the restore never took the next free number. Masking is the
    /// <c>HRESULT_CODE</c> macro, and what it leaves behind is what keeps the answer narrow: <c>0x80070005</c>
    /// is 5, <c>0x80070020</c> is 32 and <c>0x80070002</c> is 2, none of which is a name — so a destination
    /// somebody is holding, a locked file and a source that is not there are never answered with a different
    /// one, and the one that arrives as an <see cref="UnauthorizedAccessException"/> is not asked about its code
    /// at all.
    /// </para>
    /// </remarks>
    internal static bool IsTaken(Exception failure) =>
        failure is IOException io && (io.HResult & ErrorMask) is ErrorFileExists or ErrorAlreadyExists;
}
