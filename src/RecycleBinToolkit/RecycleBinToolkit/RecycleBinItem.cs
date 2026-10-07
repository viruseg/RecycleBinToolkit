namespace RecycleBinToolkit;

/// <summary>
/// One item currently sitting in the Windows Recycle Bin.
/// </summary>
/// <param name="Name">File or directory name, as it was before deletion.</param>
/// <param name="OriginalPath">Full path the item was deleted from.</param>
/// <param name="RecycledPath">Physical path inside the Recycle Bin, the renamed <c>$R</c> entry.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="DeletedAt">When the item was deleted.</param>
/// <param name="IsDirectory">Whether the item is a directory.</param>
public sealed record RecycleBinItem(
    string Name,
    string OriginalPath,
    string RecycledPath,
    long Size,
    DateTimeOffset DeletedAt,
    bool IsDirectory);
