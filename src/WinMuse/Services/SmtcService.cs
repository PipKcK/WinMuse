using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>Hooks the Windows media overlay / media keys (System Media Transport Controls).</summary>
public sealed class SmtcService
{
    private readonly PlaybackService _player;
    private SystemMediaTransportControls? _smtc;
    private MediaPlayer? _host;
    private DateTime _lastTimeline;

    public SmtcService(PlaybackService player) => _player = player;

    public void Initialize()
    {
        try
        {
            // A muted MediaPlayer with its command manager disabled gives us an SMTC instance bound to this process.
            _host = new MediaPlayer();
            _host.CommandManager.IsEnabled = false;
            _smtc = _host.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = _smtc.IsStopEnabled = true;
            _smtc.ShuffleEnabled = _player.Shuffle;
            _smtc.ButtonPressed += OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested += (_, e) => App.UI.TryEnqueue(() => _player.Seek(e.RequestedPlaybackPosition.TotalSeconds));
            _smtc.ShuffleEnabledChangeRequested += (_, e) => App.UI.TryEnqueue(() => _player.Shuffle = e.RequestedShuffleEnabled);
            _smtc.AutoRepeatModeChangeRequested += (_, e) => App.UI.TryEnqueue(() => _player.Repeat = e.RequestedAutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.List => RepeatMode.All,
                MediaPlaybackAutoRepeatMode.Track => RepeatMode.One,
                _ => RepeatMode.Off,
            });

            _player.TrackChanged += t => _ = UpdateTrackAsync(t);
            _player.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(PlaybackService.IsPlaying): UpdateStatus(); break;
                    case nameof(PlaybackService.Position):
                        if ((DateTime.Now - _lastTimeline).TotalSeconds > 4) UpdateTimeline();
                        break;
                    case nameof(PlaybackService.Shuffle): _smtc.ShuffleEnabled = _player.Shuffle; break;
                    case nameof(PlaybackService.Repeat):
                        _smtc.AutoRepeatMode = _player.Repeat switch { RepeatMode.All => MediaPlaybackAutoRepeatMode.List, RepeatMode.One => MediaPlaybackAutoRepeatMode.Track, _ => MediaPlaybackAutoRepeatMode.None };
                        break;
                }
            };
            _player.Seeked += UpdateTimeline;
        }
        catch (Exception ex) { AppPaths.Log("SMTC init failed: " + ex.Message); }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        App.UI.TryEnqueue(async () =>
        {
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play: _player.Play(); break;
                case SystemMediaTransportControlsButton.Pause: _player.Pause(); break;
                case SystemMediaTransportControlsButton.Stop: _player.Pause(); break;
                case SystemMediaTransportControlsButton.Next: await _player.NextAsync(); break;
                case SystemMediaTransportControlsButton.Previous: await _player.PreviousAsync(); break;
            }
        });
    }

    private void UpdateStatus()
    {
        if (_smtc == null) return;
        _smtc.PlaybackStatus = _player.CurrentTrack == null ? MediaPlaybackStatus.Closed
            : _player.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        UpdateTimeline();
    }

    private void UpdateTimeline()
    {
        if (_smtc == null) return;
        _lastTimeline = DateTime.Now;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = TimeSpan.FromSeconds(_player.Position),
            MaxSeekTime = TimeSpan.FromSeconds(_player.Duration),
            EndTime = TimeSpan.FromSeconds(_player.Duration),
        });
    }

    private async Task UpdateTrackAsync(Track? t)
    {
        if (_smtc == null) return;
        try
        {
            var u = _smtc.DisplayUpdater;
            u.ClearAll();
            if (t != null)
            {
                u.Type = MediaPlaybackType.Music;
                u.AppMediaId = "WinMuse";
                u.MusicProperties.Title = t.DisplayTitle;
                u.MusicProperties.Artist = t.DisplayArtist;
                u.MusicProperties.AlbumTitle = t.DisplayAlbum;
                u.MusicProperties.AlbumArtist = t.DisplayAlbumArtist;
                if (t.TrackNumber > 0) u.MusicProperties.TrackNumber = (uint)t.TrackNumber;
                if (t.ArtPath != null && File.Exists(t.ArtPath))
                    u.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(t.ArtPath));
            }
            u.Update();
            UpdateStatus();
        }
        catch (Exception ex) { AppPaths.Log("SMTC update failed: " + ex.Message); }
    }
}
