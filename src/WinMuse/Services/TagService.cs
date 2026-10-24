using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services.Audio;

namespace WinMuse.Services;

/// <summary>Reads and writes file tags with TagLib#, falling back to FFmpeg for formats TagLib can't parse.</summary>
public sealed class TagService
{
    private readonly ArtworkService _art;

    public TagService(ArtworkService art) => _art = art;

    public Track Read(string path)
    {
        var fi = new FileInfo(path);
        var t = new Track
        {
            Path = path,
            FileSize = fi.Length,
            ModifiedUtc = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds(),
            AddedUtc = Format.NowUnix(),
        };
        byte[]? picture = null;
        bool tagLibOk = false;

        try
        {
            using var f = TagLib.File.Create(path);
            var tag = f.Tag;
            t.Title = Clean(tag.Title);
            t.Artist = Clean(tag.JoinedPerformers);
            t.AlbumArtist = Clean(tag.JoinedAlbumArtists);
            t.Album = Clean(tag.Album);
            t.Genre = Clean(tag.JoinedGenres);
            t.Year = (int)tag.Year;
            t.TrackNumber = (int)tag.Track;
            t.TrackCount = (int)tag.TrackCount;
            t.DiscNumber = (int)tag.Disc;
            t.Composer = Clean(tag.JoinedComposers);
            t.Comment = Clean(tag.Comment);
            t.Lyrics = Clean(tag.Lyrics);
            t.Bpm = (int)tag.BeatsPerMinute;

            var p = f.Properties;
            if (p != null)
            {
                t.DurationMs = (long)p.Duration.TotalMilliseconds;
                t.Bitrate = p.AudioBitrate;
                t.SampleRate = p.AudioSampleRate;
                t.Channels = p.AudioChannels;
                t.BitsPerSample = p.BitsPerSample;
                t.Codec = CodecFromTagLib(path, p.Description);
            }

            var pic = tag.Pictures?.FirstOrDefault(x => x.Type == TagLib.PictureType.FrontCover) ?? tag.Pictures?.FirstOrDefault();
            picture = pic?.Data?.Data;
            tagLibOk = true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"TagLib could not read {path}: {ex.Message}");
        }

        // FFmpeg fills the gaps: unsupported containers, missing stream info, RIFF/INFO tags TagLib skips, lossless bit depth.
        bool lossless = t.Codec is "ALAC" or "WAV" or "AIFF" or "APE" or "WavPack" or "FLAC";
        if (!tagLibOk || t.DurationMs <= 0 || t.SampleRate <= 0 || t.Artist == null || t.Title == null || (lossless && t.BitsPerSample <= 0) || picture == null)
        {
            var probe = FFmpegDecoder.Probe(path, includePicture: picture == null);
            if (probe != null)
            {
                t.Title ??= probe.Tag("title");
                t.Artist ??= probe.Tag("artist", "performer");
                t.AlbumArtist ??= probe.Tag("album_artist", "albumartist", "album artist");
                t.Album ??= probe.Tag("album");
                t.Genre ??= probe.Tag("genre");
                t.Composer ??= probe.Tag("composer");
                t.Comment ??= probe.Tag("comment", "description");
                t.Lyrics ??= probe.Tag("lyrics", "unsyncedlyrics");
                if (t.Year <= 0) t.Year = ParseLeadingInt(probe.Tag("date", "year", "originaldate"));
                if (t.TrackNumber <= 0) t.TrackNumber = ParseLeadingInt(probe.Tag("track", "tracknumber"));
                if (t.DiscNumber <= 0) t.DiscNumber = ParseLeadingInt(probe.Tag("disc", "discnumber"));
                // TagLib ignores the MP3 "Info" (CBR) header and guesses from the first frame, which can be far off.
                var probeMs = (long)probe.Duration.TotalMilliseconds;
                if (t.DurationMs <= 0 || (t.Codec == "MP3" && probeMs > 0 && Math.Abs(probeMs - t.DurationMs) > probeMs / 20))
                {
                    t.DurationMs = probeMs;
                    if (probe.Bitrate > 0) t.Bitrate = probe.Bitrate;
                }
                if (t.SampleRate <= 0) t.SampleRate = probe.SampleRate;
                if (t.Channels <= 0) t.Channels = probe.Channels;
                if (t.BitsPerSample <= 0) t.BitsPerSample = probe.BitsPerSample;
                if (t.Bitrate <= 0) t.Bitrate = probe.Bitrate;
                if (string.IsNullOrEmpty(t.Codec)) t.Codec = PrettyCodec(probe.Codec);
                picture ??= probe.AttachedPicture;
            }
        }

        if (t.Bitrate <= 0 && t.DurationMs > 0) t.Bitrate = (int)(t.FileSize * 8 / t.DurationMs);
        t.Codec ??= Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        t.ArtPath = _art.Save(picture) ?? _art.FindFolderArt(path);
        t.ArtState = t.ArtPath != null ? ItemState.FromFile : ItemState.Missing;
        t.LyricsState = !string.IsNullOrWhiteSpace(t.Lyrics) || File.Exists(Path.ChangeExtension(path, ".lrc")) ? ItemState.FromFile : ItemState.Missing;
        return t;
    }

    /// <summary>Re-reads only the embedded picture (used when the file changed).</summary>
    public byte[]? ReadPicture(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            var pic = f.Tag.Pictures?.FirstOrDefault(x => x.Type == TagLib.PictureType.FrontCover) ?? f.Tag.Pictures?.FirstOrDefault();
            if (pic != null) return pic.Data.Data;
        }
        catch { }
        return FFmpegDecoder.Probe(path, true)?.AttachedPicture;
    }

    /// <summary>Writes the edit into the file's tags. Throws if the format can't be written.</summary>
    public void Write(string path, TagEdit e)
    {
        using var f = TagLib.File.Create(path);
        var tag = f.Tag;
        if (e.Title != null) tag.Title = NullIfEmpty(e.Title);
        if (e.Artist != null) tag.Performers = Split(e.Artist);
        if (e.AlbumArtist != null) tag.AlbumArtists = Split(e.AlbumArtist);
        if (e.Album != null) tag.Album = NullIfEmpty(e.Album);
        if (e.Genre != null) tag.Genres = Split(e.Genre);
        if (e.Year != null) tag.Year = (uint)Math.Max(0, e.Year.Value);
        if (e.TrackNumber != null) tag.Track = (uint)Math.Max(0, e.TrackNumber.Value);
        if (e.TrackCount != null) tag.TrackCount = (uint)Math.Max(0, e.TrackCount.Value);
        if (e.DiscNumber != null) tag.Disc = (uint)Math.Max(0, e.DiscNumber.Value);
        if (e.Composer != null) tag.Composers = Split(e.Composer);
        if (e.Comment != null) tag.Comment = NullIfEmpty(e.Comment);
        if (e.Lyrics != null) tag.Lyrics = NullIfEmpty(e.Lyrics);
        if (e.Bpm != null) tag.BeatsPerMinute = (uint)Math.Max(0, e.Bpm.Value);
        if (e.Artwork != null)
        {
            tag.Pictures = e.Artwork.Length == 0
                ? Array.Empty<TagLib.IPicture>()
                : new TagLib.IPicture[]
                {
                    new TagLib.Picture(new TagLib.ByteVector(e.Artwork))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = ArtworkService.MimeFor(e.Artwork),
                        Description = "Cover",
                    },
                };
        }
        f.Save();
    }

    private static string[] Split(string s) =>
        string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Trim('\0');

    private static int ParseLeadingInt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : 0;
    }

    private static string CodecFromTagLib(string path, string? description)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var d = description?.ToLowerInvariant() ?? "";
        return ext switch
        {
            ".m4a" or ".m4b" or ".mp4" => d.Contains("alac") ? "ALAC" : "AAC",
            ".ogg" or ".oga" => d.Contains("opus") ? "Opus" : d.Contains("flac") ? "FLAC" : "Vorbis",
            ".opus" => "Opus",
            ".wv" => "WavPack",
            ".ape" => "APE",
            ".mpc" => "Musepack",
            ".aif" or ".aiff" or ".aifc" => "AIFF",
            ".wav" or ".wave" => "WAV",
            ".dsf" or ".dff" => "DSD",
            _ => ext.TrimStart('.').ToUpperInvariant(),
        };
    }

    private static string PrettyCodec(string ffName) => ffName.ToLowerInvariant() switch
    {
        "mp3" or "mp3float" => "MP3",
        "aac" => "AAC",
        "alac" => "ALAC",
        "flac" => "FLAC",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "wmav2" or "wmav1" or "wmapro" or "wmalossless" => "WMA",
        "dsd_lsbf" or "dsd_msbf" or "dsd_lsbf_planar" or "dsd_msbf_planar" => "DSD",
        "tta" => "TTA",
        "wavpack" => "WavPack",
        "ape" => "APE",
        "tak" => "TAK",
        "ac3" => "AC-3",
        "eac3" => "E-AC-3",
        "dts" => "DTS",
        "truehd" => "TrueHD",
        "mlp" => "MLP",
        var s when s.StartsWith("pcm_") => "PCM",
        var s => s.ToUpperInvariant(),
    };
}
