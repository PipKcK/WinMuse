using System.Runtime.InteropServices;

namespace WinMuse.Helpers;

/// <summary>File Explorer's name order: numbers inside names compare by value ("Mix 2" before "Mix 10").</summary>
public sealed partial class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
