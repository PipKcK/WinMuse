using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinMuseSetup;

/// <summary>
/// The app travels inside Setup.exe: the exe, then a zip of the app, then the zip's length (8 bytes) and a marker
/// (8 bytes). Make-Installer.ps1 writes that tail; an exe without it (the installed Uninstall.exe) has no payload.
/// </summary>
internal static class Payload
{
    public const string Marker = "WMSETUP1";

    /// <summary>Where the zip starts and how long it is, or null when the exe carries no app.</summary>
    public static (long Offset, long Length)? Find(string exe)
    {
        using var fs = File.OpenRead(exe);
        if (fs.Length < 16) return null;
        var tail = new byte[16];
        fs.Seek(-16, SeekOrigin.End);
        fs.Read(tail, 0, 16);
        if (Encoding.ASCII.GetString(tail, 8, 8) != Marker) return null;
        var length = BitConverter.ToInt64(tail, 0);
        var offset = fs.Length - 16 - length;
        return offset > 0 ? (offset, length) : null;
    }

    /// <summary>Settings read from setup.ini (Key=Value lines).</summary>
    public static Dictionary<string, string> ParseIni(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.TrimStart('﻿').Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
        return d;
    }
}

/// <summary>A read-only window onto part of another stream (the zip inside Setup.exe).</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start, _length;
    private long _pos;

    public SubStream(Stream inner, long start, long length) { _inner = inner; _start = start; _length = length; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _pos; set => _pos = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        count = (int)Math.Min(count, _length - _pos);
        if (count <= 0) return 0;
        _inner.Position = _start + _pos;
        var n = _inner.Read(buffer, offset, count);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _length + offset };
        return _pos;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}
