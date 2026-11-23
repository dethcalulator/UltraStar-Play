using System;
using System.Linq;
using LibVLCSharp;
using UnityEngine;
using Object = UnityEngine.Object;

/**
 * Wraps a VLCMediaPlayer of the VLC for Unity plugin.
 * Provides millisecond based playback control and works around known libVLC issues
 * (see https://github.com/UltraStar-Deluxe/Play/issues/462).
 */
public class VlcPlayback : IDisposable
{
    private const long MicrosecondsPerMillisecond = 1000;

    // Maximum time to extrapolate the playback position beyond the last time reported by libVLC.
    private const double MaxExtrapolationInMillis = 500;

    // Maximum backward correction of the extrapolated position that is hidden to keep the position monotonic.
    private const double MaxHiddenBackwardCorrectionInMillis = 100;

    private const float PendingSeekTimeoutInSeconds = 1;

    // libVLC has completed a seek when its time is this close to the expected position.
    private const double SeekCompletedToleranceInMillis = 250;

    // libVLC 4 buffers 1000 ms by default, which delays the playback after seeking.
    private const string FileCachingOption = ":file-caching=300";

    private const float LoadTimeoutInSeconds = 10;
    private const float PausedLengthTimeoutInSeconds = 1;

    private readonly GameObject gameObject;

    public VLCMediaPlayer VlcMediaPlayer { get; private set; }
    private MediaPlayer MediaPlayer => VlcMediaPlayer != null ? VlcMediaPlayer.MediaPlayer : null;

    // Last time reported by libVLC and when it changed, used to extrapolate a smooth playback position.
    // libVLC updates its time only in steps.
    private double lastVlcTimeInMillis = -1;
    private float lastVlcTimeChangeInSeconds;
    private double lastPositionInMillis;

    // Position that has been set but is not yet reflected by the time of libVLC.
    private double? pendingSeekPositionInMillis;
    private float seekTimeInSeconds;

    private bool hasReachedEnd;

    // libVLC needs some time to leave the stopped state after restarting the playback.
    private bool isRestarting;

    // libVLC 4 reports the end of the media as "stopped". This is used to distinguish it from a requested stop.
    private bool isStopRequested;

    public double DurationInMillis { get; private set; }

    public VlcPlayback(Transform parent, string name, Settings settings)
    {
        // The VLCMediaPlayer reads its configuration in Awake.
        // Thus, create it on an inactive GameObject and activate the GameObject after configuration.
        gameObject = new GameObject(name);
        gameObject.SetActive(false);
        gameObject.transform.SetParent(parent, false);

        VlcMediaPlayer = gameObject.AddComponent<VLCMediaPlayer>();
        VlcMediaPlayer.playOnAwake = false;
        VlcMediaPlayer.logPlayerActivity = settings.LogVlcOutput;
        VlcMediaPlayer.libVLCArguments = GetLibVlcArguments(settings);

        gameObject.SetActive(true);

        // Note: The state is polled instead of using libVLC events (e.g. MediaPlayer.Stopped, MediaPlayer.WatchTime).
        // Events are called from libVLC threads and would have to be dispatched to the main thread anyway.
    }

    private static string[] GetLibVlcArguments(Settings settings)
    {
        string[] options = settings.VlcOptions
            .Where(option => !option.IsNullOrEmpty())
            .ToArray();

#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
        // LibVLCSharp registers managed callbacks for all media player events (e.g. audio volume changed).
        // The PulseAudio output of libVLC calls some of them from the PulseAudio mainloop thread,
        // which blocks all signals. Mono cannot suspend this thread for garbage collection anymore,
        // such that the next garbage collection freezes the application.
        // The ALSA output calls these callbacks from libVLC threads, which is fine.
        if (!options.Any(option => option.Trim().StartsWith("--aout")))
        {
            options = options.Append("--aout=alsa").ToArray();
        }
#endif

        return options;
    }

    public async Awaitable LoadAsync(string uri, double startPositionInMillis, params string[] mediaOptions)
    {
        ResetTimeState();
        DurationInMillis = 0;
        isStopRequested = false;

        // Start paused. Playback is started explicitly by the caller.
        string[] options = mediaOptions
            .Append(":start-paused")
            .Prepend(FileCachingOption)
            .ToArray();
        await VlcMediaPlayer.OpenAsync(uri, options);
        if (VlcMediaPlayer == null)
        {
            throw new DestroyedAlreadyException($"Failed to load '{uri}' via libVLC: media player has been destroyed already.");
        }

        await WaitForLengthAsync(PausedLengthTimeoutInSeconds);
        if (VlcMediaPlayer == null)
        {
            throw new DestroyedAlreadyException($"Failed to load '{uri}' via libVLC: media player has been destroyed already.");
        }

        if (GetLengthInMillis() <= 0
            && MediaPlayer.State != VLCState.Error)
        {
            // Some files do not store their duration (e.g. raw AAC in ADTS format).
            // libVLC estimates the duration only during playback. Thus, play muted until the duration is known.
            VlcMediaPlayer.SetVolume(0);
            MediaPlayer.SetPause(false);
            await WaitForLengthAsync(LoadTimeoutInSeconds);
            if (VlcMediaPlayer == null)
            {
                throw new DestroyedAlreadyException($"Failed to load '{uri}' via libVLC: media player has been destroyed already.");
            }
            MediaPlayer.SetPause(true);
            VlcMediaPlayer.SetVolume(100);
        }

        DurationInMillis = GetLengthInMillis();
        if (DurationInMillis <= 0)
        {
            Stop();
            throw new VlcPlaybackException($"Failed to load '{uri}' via libVLC. State: {MediaPlayer.State}");
        }

        PositionInMillis = startPositionInMillis;
    }

    private async Awaitable WaitForLengthAsync(float timeoutInSeconds)
    {
        float startTime = Time.realtimeSinceStartup;
        await ConditionUtils.WaitForConditionAsync(() => VlcMediaPlayer == null
                                                         || GetLengthInMillis() > 0
                                                         || MediaPlayer.State == VLCState.Error
                                                         || Time.realtimeSinceStartup - startTime > timeoutInSeconds);
    }

    public void Play()
    {
        if (MediaPlayer == null)
        {
            return;
        }

        UpdateHasReachedEnd();
        if (hasReachedEnd
            || MediaPlayer.State is VLCState.Stopped or VLCState.NothingSpecial)
        {
            // libVLC stops at the end of the media. It has to be started again.
            hasReachedEnd = false;
            isStopRequested = false;
            isRestarting = true;
            VlcMediaPlayer.Play();
            return;
        }

        // Do not use MediaPlayer.Pause() because it toggles the pause state.
        // Toggling caused unexpected resume of the playback (https://code.videolan.org/videolan/vlc/-/issues/28353).
        MediaPlayer.SetPause(false);
    }

    public void Pause()
    {
        MediaPlayer?.SetPause(true);
    }

    public void Stop()
    {
        isStopRequested = true;
        VlcMediaPlayer?.Stop();
        ResetTimeState();
    }

    public bool IsPlaying
    {
        get
        {
            UpdateHasReachedEnd();
            return MediaPlayer != null
                   && !hasReachedEnd
                   && MediaPlayer.IsPlaying;
        }
    }

    public double PlaybackSpeed
    {
        get => MediaPlayer?.Rate ?? 1;
        // libVLC keeps the pitch when changing the rate (scaletempo audio filter).
        set => MediaPlayer?.SetRate((float)value);
    }

    public double VolumeFactor
    {
        get => MediaPlayer != null ? MediaPlayer.Volume / 100.0 : 1;
        // libVLC volume is in percent, where 100 = 0 dB. Values above 100 amplify the audio.
        set => VlcMediaPlayer?.SetVolume(Mathf.Clamp((int)Math.Round(value * 100), 0, 200));
    }

    public double PositionInMillis
    {
        get
        {
            if (MediaPlayer == null)
            {
                return 0;
            }

            UpdateHasReachedEnd();
            if (hasReachedEnd)
            {
                // libVLC resets the time after the end of the media.
                return DurationInMillis;
            }

            double vlcTimeInMillis = Math.Max(0, (double)MediaPlayer.Time / MicrosecondsPerMillisecond);
            if (pendingSeekPositionInMillis.HasValue)
            {
                // libVLC may still report times from before the seek. Advance the target position while playing.
                float timeSinceSeekInSeconds = Time.realtimeSinceStartup - seekTimeInSeconds;
                double expectedPositionInMillis = MediaPlayer.IsPlaying
                    ? pendingSeekPositionInMillis.Value + timeSinceSeekInSeconds * 1000.0 * PlaybackSpeed
                    : pendingSeekPositionInMillis.Value;
                if (Math.Abs(vlcTimeInMillis - expectedPositionInMillis) > SeekCompletedToleranceInMillis
                    && timeSinceSeekInSeconds < PendingSeekTimeoutInSeconds)
                {
                    lastPositionInMillis = expectedPositionInMillis;
                    return expectedPositionInMillis;
                }
                pendingSeekPositionInMillis = null;
            }

            float nowInSeconds = Time.realtimeSinceStartup;
            if (vlcTimeInMillis != lastVlcTimeInMillis)
            {
                lastVlcTimeInMillis = vlcTimeInMillis;
                lastVlcTimeChangeInSeconds = nowInSeconds;
            }

            if (!MediaPlayer.IsPlaying)
            {
                lastPositionInMillis = vlcTimeInMillis;
                return vlcTimeInMillis;
            }

            double extrapolationInMillis = Math.Min(
                (nowInSeconds - lastVlcTimeChangeInSeconds) * 1000.0 * PlaybackSpeed,
                MaxExtrapolationInMillis);
            double positionInMillis = lastVlcTimeInMillis + extrapolationInMillis;

            // Keep the position monotonic when libVLC corrects the extrapolation by a small amount.
            if (positionInMillis < lastPositionInMillis
                && lastPositionInMillis - positionInMillis < MaxHiddenBackwardCorrectionInMillis)
            {
                return lastPositionInMillis;
            }

            lastPositionInMillis = positionInMillis;
            return positionInMillis;
        }
        set => SeekTo(value);
    }

    public Texture OutputTexture => VlcMediaPlayer != null ? VlcMediaPlayer.OutputTexture : null;

    private void SeekTo(double positionInMillis)
    {
        if (MediaPlayer == null)
        {
            return;
        }

        // libVLC jumped to the end of the media when seeking to exactly 0.
        positionInMillis = NumberUtils.Limit(positionInMillis, 1, Math.Max(1, DurationInMillis - 1));

        UpdateHasReachedEnd();
        if (hasReachedEnd)
        {
            // libVLC stops at the end of the media. It has to be started again to be able to seek.
            // The caller decides whether it should be playing afterwards.
            hasReachedEnd = false;
            isStopRequested = false;
            isRestarting = true;
            VlcMediaPlayer.Play();
            MediaPlayer.SetPause(true);
        }

        pendingSeekPositionInMillis = positionInMillis;
        seekTimeInSeconds = Time.realtimeSinceStartup;
        lastVlcTimeInMillis = -1;
        lastPositionInMillis = positionInMillis;

        // Use precise seeking (not fast seeking) because the position is relevant for lyrics and scoring.
        MediaPlayer.SetTime((long)(positionInMillis * MicrosecondsPerMillisecond), false);
    }

    private double GetLengthInMillis()
    {
        if (MediaPlayer == null)
        {
            return 0;
        }

        long lengthInMicroseconds = MediaPlayer.Length;
        if (lengthInMicroseconds <= 0)
        {
            lengthInMicroseconds = VlcMediaPlayer.Duration * MicrosecondsPerMillisecond;
        }
        return Math.Max(0, (double)lengthInMicroseconds / MicrosecondsPerMillisecond);
    }

    private void ResetTimeState()
    {
        lastVlcTimeInMillis = -1;
        lastPositionInMillis = 0;
        pendingSeekPositionInMillis = null;
        hasReachedEnd = false;
    }

    private void UpdateHasReachedEnd()
    {
        if (MediaPlayer == null)
        {
            return;
        }

        VLCState state = MediaPlayer.State;
        if (isRestarting)
        {
            isRestarting = state is VLCState.Stopped or VLCState.Stopping or VLCState.NothingSpecial;
            return;
        }

        // libVLC 4 reports the end of the media as "stopped".
        if (!isStopRequested
            && DurationInMillis > 0
            && state is VLCState.Stopped or VLCState.Stopping)
        {
            hasReachedEnd = true;
        }
    }

    public void Dispose()
    {
        if (VlcMediaPlayer == null)
        {
            return;
        }

        VlcMediaPlayer = null;
        if (gameObject != null)
        {
            Object.Destroy(gameObject);
        }
    }
}
