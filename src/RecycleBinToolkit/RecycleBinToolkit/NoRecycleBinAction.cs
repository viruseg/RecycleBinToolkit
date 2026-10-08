namespace RecycleBinToolkit;

/// <summary>
/// What to do with a path on a volume that cannot recycle it. The three values run from safest to most
/// destructive.
/// </summary>
/// <remarks>
/// The states are nested rather than independent, which is why this is one enum and not two flags:
/// "do not refuse an unproven answer" is a weakening of <see cref="Throw"/>, not a separate decision.
/// Whether a refusal raises or is returned as data remains <see cref="RecycleBinOptions.ThrowOnError"/>.
/// </remarks>
public enum NoRecycleBinAction
{
    /// <summary>
    /// Refuse the path and leave the file where it is. Nothing is handed to the Shell, so nothing can
    /// remove the file behind the caller's back.
    /// </summary>
    /// <remarks>
    /// This is the default, and the only value that cannot lose data. It raises only when
    /// <see cref="RecycleBinOptions.ThrowOnError"/> is also on: with that off the refusal arrives in
    /// <see cref="RecycleBinResult.Failures"/> instead of as an exception.
    /// </remarks>
    Throw,

    /// <summary>
    /// Hand the path over even though the volume's answer was not
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// Covers <see cref="RecycleBinAvailabilityReason.Indeterminate"/> and
    /// <see cref="RecycleBinAvailabilityReason.NoRecycleBinOnVolume"/>, and with them the risk the
    /// caller is accepting: on such a volume the Shell removes the file for good and still reports
    /// success, so the path comes back in <see cref="RecycleBinResult.RecycledPaths"/> and never in
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
    /// </remarks>
    AllowIndeterminate,

    /// <summary>
    /// Remove the path for good, without asking the Shell, and list it in
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
    /// </summary>
    /// <remarks>
    /// It is never listed in <see cref="RecycleBinResult.RecycledPaths"/>: calling a permanent deletion
    /// recycled would tell the caller the opposite of what happened. The removal is this library's own
    /// rather than the Shell's, because on such a volume the Shell's only move is to destroy the file and
    /// it reports the delete as a success — which is the risk <see cref="AllowIndeterminate"/> accepts and
    /// this value does not.
    /// <para>
    /// A file system that refuses the removal is reported at
    /// <see cref="RecycleFailureStage.PermanentDeleteFailed"/> carrying its own code, and the file stays
    /// exactly where the caller left it.
    /// </para>
    /// </remarks>
    DeletePermanently
}