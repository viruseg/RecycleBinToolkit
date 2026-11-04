using System.Runtime.Versioning;
using RecycleBinToolkit;
using Sharprompt;

[assembly: SupportedOSPlatform("windows10.0.14393")]

Start:
Console.Clear();

var recycleBinContents = await RecycleBin.GetItemsAsync();

var rootWidth = recycleBinContents.Items.Select(o => o.VolumeRoot).Append("All").Max(o => o.Length);

var items = new List<MenuItem>
{
    new(BinLabel("All", recycleBinContents.Items.Sum(o => o.Items.Count), recycleBinContents.TotalSize, rootWidth), MenuAction.EmptyBin, null),
};

foreach (var volumeContents in recycleBinContents.Items)
{
    var count = volumeContents.Items.Count;
    var bytes = volumeContents.Items.Sum(o => o.Size);
    items.Add(new(BinLabel(volumeContents.VolumeRoot, count, bytes, rootWidth), MenuAction.EmptyBin, volumeContents.VolumeRoot));
}

const string RecycleLabel = "Recycle a path";

items.Add(new(new string('─', Math.Max(items.Max(o => o.Label.Length), RecycleLabel.Length)), MenuAction.DoNothing, null));
items.Add(new(RecycleLabel, MenuAction.RecyclePath, null));

Console.WriteLine($"Listed the Recycle Bin in {recycleBinContents.Elapsed.TotalSeconds:F2}s");
Console.WriteLine();

var item = Prompt.Select(
    "Select the drive whose Recycle Bin you want to empty",
    items,
    textSelector: o => o.Label);

switch (item.Action)
{
    case MenuAction.EmptyBin:
        Console.WriteLine($"Emptying: {item.Root ?? "All"} ...");
        await RecycleBin.EmptyAsync(item.Root);
        break;

    case MenuAction.DoNothing:
        break;

    case MenuAction.RecyclePath:
    {
        var path = Prompt.Input<string>("Enter the path to recycle").Trim().Trim('"').Trim();
        var failure = (await RecycleBin.RecycleAsync([path])).FirstFailure;

        Console.WriteLine();
        Console.WriteLine(failure is null
            ? $"Recycled: {path}"
            : $"Not recycled: {failure.Path} (stage={failure.Stage}, hresult=0x{failure.HResult:X8})");
        Console.WriteLine();
        Console.WriteLine("Press any key to continue . . .");
        Console.ReadKey(true);
        break;
    }
}

goto Start;

static string BinLabel(string root, int count, long bytes, int rootWidth) =>
    $"{root.PadRight(rootWidth)}  {count,8} items {FormatBytes(bytes)}";

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

record MenuItem(string Label, MenuAction Action, string? Root);

enum MenuAction
{
    EmptyBin,
    DoNothing,
    RecyclePath,
}