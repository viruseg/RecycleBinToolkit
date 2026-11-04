using System.Runtime.Versioning;
using RecycleBinToolkit;
using Sharprompt;

[assembly: SupportedOSPlatform("windows10.0.14393")]

Start:
Console.Clear();

var recycleBinContents = await RecycleBin.GetItemsAsync();

var choices = new List<DriveChoice>
{
    new(null, recycleBinContents.Items.Sum(o => o.Items.Count), recycleBinContents.TotalSize),
};

foreach (var volumeContents in recycleBinContents.Items)
{
    choices.Add(new(volumeContents.VolumeRoot, volumeContents.Items.Count, volumeContents.Items.Sum(o => o.Size)));
}

var rootWidth = choices.Max(o => (o.Root ?? "All").Length);

Console.WriteLine($"Listed the Recycle Bin in {recycleBinContents.Elapsed.TotalSeconds:F2}s");
Console.WriteLine();

var choice = Prompt.Select(
    "Select the drive whose Recycle Bin you want to empty",
    choices,
    textSelector: o => $"{(o.Root ?? "All").PadRight(rootWidth)}  {o.Count,8} items {FormatBytes(o.Bytes)}");

Console.WriteLine($"Emptying: {choice.Root ?? "All"} ...");

await RecycleBin.EmptyAsync(choice.Root);

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

record DriveChoice(string? Root, int Count, long Bytes);