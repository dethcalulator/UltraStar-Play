using UniInject;
using UnityEngine;

/**
 * Plays video via its own libVLC media player, e.g., mp4 with H.264 on Linux.
 * The audio of the video file is not played. The SongAudioPlayer is the time reference.
 */
public class VlcVideoSupportProvider : AbstractVideoSupportProvider, IVlcVideoSupportProvider
{
    [Inject]
    private Settings settings;

    private VlcPlayback vlcPlayback;
    private RenderTexture targetTexture;
    private ESongBackgroundScaleMode scaleMode = ESongBackgroundScaleMode.FitInside;

    public override async Awaitable<VideoLoadedEvent> LoadAsync(string videoUri, double startPositionInMillis)
    {
        Unload();

        vlcPlayback = new VlcPlayback(transform, $"{nameof(VlcVideoSupportProvider)}-VLCMediaPlayer", settings);
        try
        {
            await vlcPlayback.LoadAsync(videoUri, startPositionInMillis, ":no-audio");
        }
        catch (VlcPlaybackException ex)
        {
            Unload();
            throw new VideoSupportProviderException(ex.Message);
        }

        if (!this)
        {
            throw new DestroyedAlreadyException($"Failed to load video '{videoUri}': {nameof(VlcVideoSupportProvider)} has been destroyed already.");
        }

        return new VideoLoadedEvent(videoUri);
    }

    private void LateUpdate()
    {
        if (vlcPlayback == null)
        {
            return;
        }

        if (IsLooping
            && !vlcPlayback.IsPlaying
            && vlcPlayback.PositionInMillis >= vlcPlayback.DurationInMillis)
        {
            vlcPlayback.PositionInMillis = 0;
            vlcPlayback.Play();
        }

        VlcRenderTextureUtils.DrawWithAspectRatio(vlcPlayback.OutputTexture, targetTexture, scaleMode);
    }

    private void OnDestroy()
    {
        // The libVLC media player must be released properly before its GameObject is destroyed.
        // Otherwise, libVLC aborts because the time watcher is still registered.
        Unload();
    }

    public override void Unload()
    {
        vlcPlayback?.Dispose();
        vlcPlayback = null;
        RenderTextureUtils.Clear(targetTexture);
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

    public override void SetTargetTexture(RenderTexture renderTexture)
    {
        targetTexture = renderTexture;
    }

    public override void SetBackgroundScaleMode(ESongBackgroundScaleMode mode)
    {
        scaleMode = mode;
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

    public override bool IsLooping { get; set; }

    public override double PlaybackSpeed
    {
        get => vlcPlayback?.PlaybackSpeed ?? 1;
        set
        {
            if (vlcPlayback != null)
            {
                vlcPlayback.PlaybackSpeed = value;
            }
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
}
