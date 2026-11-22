using UnityEngine;

/**
 * Shows the video of the libVLC media player that is used by the SongAudioPlayer.
 * Used when the audio file is also the video file, such that the file is only decoded once.
 * Playback (play, pause, position) is controlled by the SongAudioPlayer.
 */
public class SongAudioPlayerVlcVideoSupportProvider : AbstractVideoSupportProvider, ISongAudioPlayerVlcVideoSupportProvider
{
    private VlcAudioSupportProvider vlcAudioSupportProvider;
    private RenderTexture targetTexture;
    private ESongBackgroundScaleMode scaleMode = ESongBackgroundScaleMode.FitInside;

    private VlcPlayback VlcPlayback => vlcAudioSupportProvider != null
        ? vlcAudioSupportProvider.VlcPlayback
        : null;

    public override Awaitable<VideoLoadedEvent> LoadAsync(string videoUri, double startPositionInMillis)
    {
        SongVideoPlayer songVideoPlayer = GetComponentInParent<SongVideoPlayer>();
        vlcAudioSupportProvider = songVideoPlayer != null && songVideoPlayer.songAudioPlayer != null
            ? songVideoPlayer.songAudioPlayer.CurrentAudioSupportProvider as VlcAudioSupportProvider
            : null;
        if (VlcPlayback == null)
        {
            throw new VideoSupportProviderException($"Failed to load video '{videoUri}': SongAudioPlayer does not use libVLC.");
        }

        AwaitableCompletionSource<VideoLoadedEvent> completionSource = new();
        completionSource.SetResult(new VideoLoadedEvent(videoUri));
        return completionSource.Awaitable;
    }

    private void LateUpdate()
    {
        if (VlcPlayback == null)
        {
            return;
        }

        VlcRenderTextureUtils.DrawWithAspectRatio(VlcPlayback.OutputTexture, targetTexture, scaleMode);
    }

    public override void Unload()
    {
        vlcAudioSupportProvider = null;
        RenderTextureUtils.Clear(targetTexture);
    }

    public override void Play()
    {
        // Controlled by SongAudioPlayer
    }

    public override void Pause()
    {
        // Controlled by SongAudioPlayer
    }

    public override void Stop()
    {
        // Controlled by SongAudioPlayer
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
        get => VlcPlayback?.IsPlaying ?? false;
        set
        {
            // Controlled by SongAudioPlayer
        }
    }

    public override bool IsLooping
    {
        get => false;
        set
        {
            // Not supported because audio and video are the same media.
        }
    }

    public override double PlaybackSpeed
    {
        get => VlcPlayback?.PlaybackSpeed ?? 1;
        set
        {
            // Controlled by SongAudioPlayer
        }
    }

    public override double PositionInMillis
    {
        get => VlcPlayback?.PositionInMillis ?? 0;
        set
        {
            // Controlled by SongAudioPlayer
        }
    }

    public override double DurationInMillis => VlcPlayback?.DurationInMillis ?? 0;
}
