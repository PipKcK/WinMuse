using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinMuse.Helpers;

/// <summary>
/// Plain-language descriptions of errors for people using the app. The exception itself (type, message, stack) goes to
/// the log; the interface shows what happened and what to try.
/// </summary>
public static class ErrorText
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    /// <summary>One or two sentences for the interface. Never the exception's own message.</summary>
    public static string Describe(Exception ex) => Unwrap(ex) switch
    {
        UnauthorizedAccessException => "Access was denied. Check that the file isn't read-only and that the app can use its folder.",
        FileNotFoundException or DirectoryNotFoundException => "The file or folder is no longer there.",
        DriveNotFoundException => "The drive isn't connected.",
        PathTooLongException => "The file's path is too long.",
        IOException io when io.HResult is SharingViolation or LockViolation => "The file is open in another app. Close it there and try again.",
        IOException io when io.HResult is DiskFull or HandleDiskFull => "The disk is full.",
        IOException => "The file couldn't be read or saved.",
        HttpRequestException => "The online service couldn't be reached. Check your internet connection and try again.",
        TaskCanceledException or TimeoutException => "The online service took too long to answer. Try again later.",
        JsonException => "The online service sent an answer the app couldn't read.",
        TagLib.CorruptFileException => "The file's tags can't be read. The file may be damaged.",
        TagLib.UnsupportedFormatException => "This file format doesn't support editing its details.",
        COMException => "Windows couldn't complete the request.",
        OperationCanceledException => "The operation was cancelled.",
        _ => "Something went wrong. Details were saved to the log.",
    };

    /// <summary>Logs the full exception (with where it happened) and returns its plain description.</summary>
    public static string Logged(Exception ex, [System.Runtime.CompilerServices.CallerMemberName] string where = "",
        [System.Runtime.CompilerServices.CallerFilePath] string file = "")
    {
        AppPaths.Log($"{Path.GetFileNameWithoutExtension(file)}.{where}: {ex}");
        return Describe(ex);
    }

    /// <summary>
    /// Errors from files, the network or online services: the app's own state is unaffected, so it keeps running
    /// normally. Anything else is a bug, after which a restart is offered.
    /// </summary>
    public static bool IsRecoverable(Exception ex) => Unwrap(ex) is IOException or UnauthorizedAccessException or HttpRequestException
        or TaskCanceledException or TimeoutException or JsonException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException
        or COMException or OperationCanceledException;

    private static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } a) ex = a.InnerExceptions[0];
        if (ex is System.Reflection.TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex;
    }
}
