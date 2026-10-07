using Microsoft.Win32;
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Covers the registry facts behind <see cref="RecycleBin.GetStatusAsync"/>.
/// </summary>
/// <remarks>
/// These read real values under a scratch key this suite creates and always deletes, because the only
/// honest test of "a setting stored as a number or as text is honoured" needs a value of each kind to
/// exist. A scratch key is used rather than the machine's own settings so nothing outside the suite's
/// own subtree is ever read or written.
/// </remarks>
internal static class RegistrySuite
{
    private const int CurrentUser = unchecked((int)0x80000001);

    private static readonly string ScratchSubKey = $@"Software\RecycleBinToolkit.Tests\{Guid.NewGuid():N}";

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("RegistrySuite." + nameof(NumericFlagIsRead), NumericFlagIsRead),
        new TestCase("RegistrySuite." + nameof(TextFlagIsRead), TextFlagIsRead),
        new TestCase("RegistrySuite." + nameof(TextFalseIsNotAFlag), TextFalseIsNotAFlag),
        new TestCase("RegistrySuite." + nameof(MissingValueIsNotAFlag), MissingValueIsNotAFlag),
        new TestCase("RegistrySuite." + nameof(ForeignTypeIsNotAFlag), ForeignTypeIsNotAFlag),
    ];

    private static Task NumericFlagIsRead(TestContext context)
    {
        WithScratchKey(key =>
        {
            key.SetValue("DwordOn", 1, RegistryValueKind.DWord);
            key.SetValue("DwordOff", 0, RegistryValueKind.DWord);

            Check.True(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "DwordOn"),
                "a REG_DWORD of 1 is a flag being set");
            Check.False(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "DwordOff"),
                "a REG_DWORD of 0 is a flag being clear");
        });

        return Task.CompletedTask;
    }

    private static Task TextFlagIsRead(TestContext context)
    {
        WithScratchKey(key =>
        {
            key.SetValue("TextOn", "1", RegistryValueKind.String);
            key.SetValue("TextTrue", "true", RegistryValueKind.String);

            Check.True(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "TextOn"),
                "a REG_SZ of \"1\" is a flag being set");
            Check.True(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "TextTrue"),
                "a REG_SZ of \"true\" is a flag being set");
        });

        return Task.CompletedTask;
    }

    private static Task TextFalseIsNotAFlag(TestContext context)
    {
        WithScratchKey(key =>
        {
            key.SetValue("TextFalse", "false", RegistryValueKind.String);
            key.SetValue("TextZero", "0", RegistryValueKind.String);

            Check.False(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "TextFalse"),
                "a REG_SZ of \"false\" is not a flag");
            Check.False(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "TextZero"),
                "a REG_SZ of \"0\" is not a flag");
        });

        return Task.CompletedTask;
    }

    private static Task MissingValueIsNotAFlag(TestContext context)
    {
        WithScratchKey(_ =>
        {
            Check.False(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "NotWritten"),
                "a value that was never written is not a flag");
            Check.False(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "NotWritten"),
                "and asking twice gives the same answer");
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// A value of a type the flag never takes must not be read as truthy. Nothing writes one of these,
    /// so the only way to be sure the reader is not lenient about types is to write one deliberately.
    /// </summary>
    private static Task ForeignTypeIsNotAFlag(TestContext context)
    {
        WithScratchKey(key =>
        {
            key.SetValue("Binary", new byte[] { 1, 0, 0, 0 }, RegistryValueKind.Binary);

            Check.False(
                RecycleBinRegistry.IsFlagSet(CurrentUser, ScratchSubKey, "Binary"),
                "a REG_BINARY is not a flag, even when its bytes would read as true");
        });

        return Task.CompletedTask;
    }

    private static void WithScratchKey(Action<RegistryKey> body)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ScratchSubKey, writable: true)!;
            body(key);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(ScratchSubKey, throwOnMissingSubKey: false);
        }
    }
}