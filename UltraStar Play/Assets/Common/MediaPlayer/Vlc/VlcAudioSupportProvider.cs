using System.IO;
using UnityEngine;

/**
 * Plays audio via libVLC, e.g., m4a or the audio of mp4 files on Linux.
 * The audio is played by libVLC directly, not via a Unity AudioSource.
 */
public class VlcAudioSupportProvider : AbstractAudioSupportProvider, IVlcAudioSupportProvider
{
    private VlcPlayback vlcPlayback;
    public VlcPlayback VlcPlayback => vlcPlayback;

    public override async Awaitable<AudioLoadedEvent> LoadAsync(string audioUri, bool streamAudio, double startPositionInMillis)
    {
        Unload();

        vlcPlayback = new VlcPlayback(transform, $"{nameof(VlcAudioSupportProvider)}-VLCMediaPlayer", settings);

        // Video is only decoded for video files,
        // such that the video can be shown via SongAudioPlayerVlcVideoSupportProvider if the video file is also the audio file.
        bool isVideoFile = ApplicationUtils.IsVlcSupportedVideoFormat(Path.GetExtension(audioUri));
        string[] mediaOptions = isVideoFile
            ? new string[] { }
            : new[] { ":no-video" };

        try
        {
            await vlcPlayback.LoadAsync(audioUri, startPositionInMillis, mediaOptions);
        }
        catch (VlcPlaybackException ex)
        {
            Unload();
            throw new AudioSupportProviderException(ex.Message);
        }

        if (!this)
        {
            throw new DestroyedAlreadyException($"Failed to load audio '{audioUri}': {nameof(VlcAudioSupportProvider)} has been destroyed already.");
        }

        return new AudioLoadedEvent(audioUri);
    }

    public override void Unload()
    {
        vlcPlayback?.Dispose();
        vlcPlayback = null;
    }

    public override void Play()
    {
        vlcPlayback?.Play();
    }

    public override void Pause()
    {
        vlcPlayback?.Pause();
    }

    public override void Stop()
    {
        vlcPlayback?.Stop();
    }

    public override bool IsPlaying
    {
        get => vlcPlayback?.IsPlaying ?? false;
        set
        {
            if (value)
            {
                Play();
            }
            else
            {
                Pause();
            }
        }
    }

    public override double PlaybackSpeed
    {
        get => vlcPlayback?.PlaybackSpeed ?? 1;
        set => SetPlaybackSpeed(value, true);
    }

    public override void SetPlaybackSpeed(double newValue, bool changeTempoButKeepPitch)
    {
        // libVLC always keeps the pitch when changing the playback rate.
        if (vlcPlayback != null)
        {
            vlcPlayback.PlaybackSpeed = newValue;
        }
    }

    public override double PositionInMillis
    {
        get => vlcPlayback?.PositionInMillis ?? 0;
        set
        {
            if (vlcPlayback != null)
            {
                vlcPlayback.PositionInMillis = value;
            }
        }
    }

    public override double DurationInMillis => vlcPlayback?.DurationInMillis ?? 0;

    public override double VolumeFactor
    {
        get => vlcPlayback?.VolumeFactor ?? 1;
        set
        {
            if (vlcPlayback != null)
            {
                vlcPlayback.VolumeFactor = value;
            }
        }
    }
}
