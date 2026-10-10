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
    Throw = 0,

    /// <summary>
    /// Hand the path over even though the volume's answer was not
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// Covers <see cref="RecycleBinAvailabilityReason.Indeterminate"/> and
    /// <see cref="RecycleBinAvailabilityReason.NoRecycleBinOnVolume"/>, and with them the risk the
    /// caller is accepting: on a volume the Shell reports no bin for it removes the file for good and
    /// still reports success, so the path comes back in <see cref="RecycleBinResult.RecycledPaths"/>
    /// and never in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>. The two reasons are not the
    /// same risk — an <see cref="RecycleBinAvailabilityReason.Indeterminate"/> volume is most often one
    /// whose probe found a bin, which recycles normally.
    /// <para>
    /// The engine can create the per-user directory under <c>$RECYCLE.BIN</c> and recycle into it, so the
    /// file survives — and the volume, which had no Recycle Bin, now has one. That is a change to the
    /// volume a caller did not ask for and is visible in Explorer. A caller who does not want a bin
    /// created on that volume wants <see cref="DeletePermanently"/>, which says so, or
    /// <see cref="Throw"/>, which refuses.
    /// </para>
    /// </remarks>
    AllowIndeterminate = 1,

    /// <summary>
    /// Remove the path for good, without asking the Shell, and list it in
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> unless the file system refuses.
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
    DeletePermanently = 2
}