using System.Runtime.Versioning;
using RecycleBinToolkit;
using Sharprompt;

[assembly: SupportedOSPlatform("windows10.0.14393")]

Start:
Console.Clear();

var recycleBinContents = await RecycleBin.GetItemsAsync();

Console.WriteLine($"The Recycle Bin contains {recycleBinContents.Items.Select(o => o.Items.Count).Sum()} items");

foreach (var volumeContents in recycleBinContents.Items)
{
    Console.WriteLine($"    {volumeContents.VolumeRoot}: {volumeContents.Items.Count} items");
}

Console.WriteLine($"Total size: {FormatBytes(recycleBinContents.TotalSize)}");
Console.WriteLine($"Listed the Recycle Bin in {recycleBinContents.Elapsed.TotalSeconds:F2}s");
Console.WriteLine();

var drive = Prompt.Select("Select the drive whose Recycle Bin you want to empty", ["All", ..recycleBinContents.Items.Select(o => o.VolumeRoot)]);

Console.WriteLine($"Emptying: {drive} ...");

if (drive == "All") drive = null;

await RecycleBin.EmptyAsync(drive);

goto Start;

static string FormatBytes(long bytes, int decimals = 2)
{
    ArgumentOutOfRangeException.ThrowIfNegative(bytes);

    const long KB = 1024L;
    const long MB = KB * 1024;
    const long GB = MB * 1024;
    const long TB = GB * 1024;
    const long PB = TB * 1024;

    double size;
    string unit;

    switch (bytes)
    {
        case >= PB: size = (double) bytes / PB; unit = "PB"; break;
        case >= TB: size = (double) bytes / TB; unit = "TB"; break;
        case >= GB: size = (double) bytes / GB; unit = "GB"; break;
        case >= MB: size = (double) bytes / MB; unit = "MB"; break;
        case >= KB: size = (double) bytes / KB; unit = "KB"; break;
        default: size = bytes; unit = "B"; break;
    }

    return $"{Math.Round(size, decimals)}{unit}";
}