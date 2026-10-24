using Dapper;
using Microsoft.Data.Sqlite;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>SQLite persistence for tracks and playlists.</summary>
public sealed class LibraryDatabase
{
    private readonly string _cs = new SqliteConnectionStringBuilder { DataSource = AppPaths.DatabasePath, Cache = SqliteCacheMode.Shared }.ToString();

    private const string TrackColumns =
        "Path, Title, Artist, AlbumArtist, Album, Genre, Year, TrackNumber, TrackCount, DiscNumber, Composer, Comment, Lyrics, Bpm, " +
        "DurationMs, Bitrate, SampleRate, Channels, BitsPerSample, Codec, FileSize, ModifiedUtc, AddedUtc, PlayCount, LastPlayedUtc, Rating, ArtPath, EditedLocally, AutoFixed, MetaState, ArtState, LyricsState";

    public LibraryDatabase()
    {
        using var c = Open();
        c.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Tracks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                Title TEXT, Artist TEXT, AlbumArtist TEXT, Album TEXT, Genre TEXT,
                Year INTEGER DEFAULT 0, TrackNumber INTEGER DEFAULT 0, TrackCount INTEGER DEFAULT 0, DiscNumber INTEGER DEFAULT 0,
                Composer TEXT, Comment TEXT, Lyrics TEXT, Bpm INTEGER DEFAULT 0,
                DurationMs INTEGER DEFAULT 0, Bitrate INTEGER DEFAULT 0, SampleRate INTEGER DEFAULT 0, Channels INTEGER DEFAULT 0,
                BitsPerSample INTEGER DEFAULT 0, Codec TEXT, FileSize INTEGER DEFAULT 0, ModifiedUtc INTEGER DEFAULT 0,
                AddedUtc INTEGER DEFAULT 0, PlayCount INTEGER DEFAULT 0, LastPlayedUtc INTEGER DEFAULT 0, Rating INTEGER DEFAULT 0,
                ArtPath TEXT, EditedLocally INTEGER DEFAULT 0, AutoFixed INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS Playlists (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL, Description TEXT, CreatedUtc INTEGER, IsSmart INTEGER DEFAULT 0, RulesJson TEXT, CoverPath TEXT
            );
            CREATE TABLE IF NOT EXISTS PlaylistItems (
                PlaylistId INTEGER NOT NULL, TrackId INTEGER NOT NULL, Position INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PlaylistItems ON PlaylistItems(PlaylistId, Position);
            """);

        // Migrations for databases created by earlier versions.
        var columns = c.Query<string>("SELECT name FROM pragma_table_info('Tracks')").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("AutoFixed")) c.Execute("ALTER TABLE Tracks ADD COLUMN AutoFixed INTEGER DEFAULT 0");
        foreach (var col in new[] { "MetaState", "ArtState", "LyricsState" })
            if (!columns.Contains(col)) c.Execute($"ALTER TABLE Tracks ADD COLUMN {col} INTEGER DEFAULT 0");
        c.Execute("CREATE TABLE IF NOT EXISTS ArtistImages (Name TEXT PRIMARY KEY COLLATE NOCASE, Path TEXT, Tried INTEGER DEFAULT 0)");
        var artistColumns = c.Query<string>("SELECT name FROM pragma_table_info('ArtistImages')").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!artistColumns.Contains("ByUser")) c.Execute("ALTER TABLE ArtistImages ADD COLUMN ByUser INTEGER DEFAULT 0");
        c.Execute("CREATE TABLE IF NOT EXISTS ExcludedPaths (Path TEXT PRIMARY KEY COLLATE NOCASE)");
        // Per-song preferences, keyed by path so they survive rescans: a saved equalizer and a lyrics offset.
        c.Execute("CREATE TABLE IF NOT EXISTS SongSettings (Path TEXT PRIMARY KEY COLLATE NOCASE, EqBands TEXT, EqPreamp REAL DEFAULT 0, LyricsOffsetMs INTEGER DEFAULT 0)");
    }

    public List<(string Name, string? Path, bool Tried, bool ByUser)> LoadArtistImages()
    {
        using var c = Open();
        return c.Query<(string, string?, bool, bool)>("SELECT Name, Path, Tried, ByUser FROM ArtistImages").ToList();
    }

    public void SaveArtistImage(string name, string? path, bool tried, bool byUser)
    {
        using var c = Open();
        c.Execute("INSERT INTO ArtistImages (Name, Path, Tried, ByUser) VALUES (@name, @path, @tried, @byUser) ON CONFLICT(Name) DO UPDATE SET Path=@path, Tried=@tried, ByUser=@byUser",
            new { name, path, tried, byUser });
    }

    public List<(string Path, string? EqBands, double EqPreamp, long LyricsOffsetMs)> LoadSongSettings()
    {
        using var c = Open();
        return c.Query<(string, string?, double, long)>("SELECT Path, EqBands, EqPreamp, LyricsOffsetMs FROM SongSettings").ToList();
    }

    public void SaveSongSettings(string path, string? eqBands, double eqPreamp, long lyricsOffsetMs)
    {
        using var c = Open();
        if (eqBands == null && lyricsOffsetMs == 0) { c.Execute("DELETE FROM SongSettings WHERE Path=@path", new { path }); return; }
        c.Execute("INSERT INTO SongSettings (Path, EqBands, EqPreamp, LyricsOffsetMs) VALUES (@path, @eqBands, @eqPreamp, @lyricsOffsetMs) " +
                  "ON CONFLICT(Path) DO UPDATE SET EqBands=@eqBands, EqPreamp=@eqPreamp, LyricsOffsetMs=@lyricsOffsetMs",
            new { path, eqBands, eqPreamp, lyricsOffsetMs });
    }

    // Songs removed from the library (files kept) are remembered so rescans don't add them back.
    public HashSet<string> LoadExcluded()
    {
        using var c = Open();
        return c.Query<string>("SELECT Path FROM ExcludedPaths").ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void AddExcluded(IEnumerable<string> paths)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var path in paths) c.Execute("INSERT OR IGNORE INTO ExcludedPaths (Path) VALUES (@path)", new { path }, tx);
        tx.Commit();
    }

    public void ClearExcluded()
    {
        using var c = Open();
        c.Execute("DELETE FROM ExcludedPaths");
    }

    public void ResetArtistImageAttempts()
    {
        using var c = Open();
        c.Execute("UPDATE ArtistImages SET Tried=0 WHERE Path IS NULL");
    }

    public void SetAutoFixed(IEnumerable<Track> tracks, bool value)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var t in tracks) c.Execute("UPDATE Tracks SET AutoFixed=@v WHERE Id=@Id", new { v = value, t.Id }, tx);
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    public List<Track> LoadTracks()
    {
        using var c = Open();
        return c.Query<Track>("SELECT Id, " + TrackColumns + " FROM Tracks").ToList();
    }

    public void UpsertTracks(IEnumerable<Track> tracks)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var cols = TrackColumns.Split(", ");
        var insert = $"INSERT INTO Tracks ({TrackColumns}) VALUES ({string.Join(", ", cols.Select(x => "@" + x))}) RETURNING Id";
        var update = $"UPDATE Tracks SET {string.Join(", ", cols.Select(x => $"{x}=@{x}"))} WHERE Id=@Id";
        foreach (var t in tracks)
        {
            if (t.Id == 0) t.Id = c.ExecuteScalar<long>(insert, t, tx);
            else c.Execute(update, t, tx);
        }
        tx.Commit();
    }

    public void DeleteTracks(IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            c.Execute("DELETE FROM Tracks WHERE Id=@id", new { id }, tx);
            c.Execute("DELETE FROM PlaylistItems WHERE TrackId=@id", new { id }, tx);
        }
        tx.Commit();
    }

    public void UpdatePlayStats(Track t)
    {
        using var c = Open();
        c.Execute("UPDATE Tracks SET PlayCount=@PlayCount, LastPlayedUtc=@LastPlayedUtc WHERE Id=@Id", t);
    }

    public void UpdateRating(Track t)
    {
        using var c = Open();
        c.Execute("UPDATE Tracks SET Rating=@Rating WHERE Id=@Id", t);
    }

    // ---- Playlists ----

    public List<(Playlist playlist, List<long> trackIds)> LoadPlaylists()
    {
        using var c = Open();
        var lists = c.Query<Playlist>("SELECT * FROM Playlists ORDER BY CreatedUtc").ToList();
        var items = c.Query<(long PlaylistId, long TrackId)>("SELECT PlaylistId, TrackId FROM PlaylistItems ORDER BY PlaylistId, Position").ToList();
        var lookup = items.ToLookup(i => i.PlaylistId, i => i.TrackId);
        return lists.Select(p => (p, lookup[p.Id].ToList())).ToList();
    }

    public void SavePlaylist(Playlist p)
    {
        using var c = Open();
        if (p.Id == 0)
            p.Id = c.ExecuteScalar<long>("INSERT INTO Playlists (Name, Description, CreatedUtc, IsSmart, RulesJson, CoverPath) VALUES (@Name, @Description, @CreatedUtc, @IsSmart, @RulesJson, @CoverPath) RETURNING Id", p);
        else
            c.Execute("UPDATE Playlists SET Name=@Name, Description=@Description, IsSmart=@IsSmart, RulesJson=@RulesJson, CoverPath=@CoverPath WHERE Id=@Id", p);
    }

    public void SetPlaylistItems(long playlistId, IEnumerable<long> trackIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM PlaylistItems WHERE PlaylistId=@playlistId", new { playlistId }, tx);
        int pos = 0;
        foreach (var id in trackIds)
            c.Execute("INSERT INTO PlaylistItems (PlaylistId, TrackId, Position) VALUES (@playlistId, @id, @pos)", new { playlistId, id, pos = pos++ }, tx);
        tx.Commit();
    }

    public void DeletePlaylist(long id)
    {
        using var c = Open();
        c.Execute("DELETE FROM PlaylistItems WHERE PlaylistId=@id; DELETE FROM Playlists WHERE Id=@id;", new { id });
    }
}
