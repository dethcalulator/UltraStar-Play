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

    // Minimum period between time updates from libVLC
    private const long WatchTimeMinimumPeriodInMicroseconds = 10_000;

    private const float LoadTimeoutInSeconds = 10;
    private const float PausedLengthTimeoutInSeconds = 1;

    private readonly GameObject gameObject;
    private readonly object timePointLock = new();

    public VLCMediaPlayer VlcMediaPlayer { get; private set; }
    private MediaPlayer MediaPlayer => VlcMediaPlayer != null ? VlcMediaPlayer.MediaPlayer : null;

    // Last time update from libVLC, used to interpolate a smooth playback position.
    private TimePoint? lastTimePoint;
    private long? pausedSystemDateInMicroseconds;

    // Position that has been set but is not yet reflected by libVLC's time updates.
    private double? pendingSeekPositionInMillis;

    private bool hasReachedEnd;

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
        VlcMediaPlayer.libVLCArguments = settings.VlcOptions
            .Where(option => !option.IsNullOrEmpty())
            .ToArray();

        gameObject.SetActive(true);

        MediaPlayer.Stopped += OnStopped;
        unsafe
        {
            MediaPlayer.WatchTime(WatchTimeMinimumPeriodInMicroseconds, OnTimeUpdate, OnTimePaused, OnTimeSeek);
        }
    }

    public async Awaitable LoadAsync(string uri, double startPositionInMillis, params string[] mediaOptions)
    {
        ResetTimeState();
        DurationInMillis = 0;
        isStopRequested = false;

        // Start paused. Playback is started explicitly by the caller.
        string[] options = mediaOptions
            .Append(":start-paused")
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
        float startTime = Time.time;
        await ConditionUtils.WaitForConditionAsync(() => VlcMediaPlayer == null
                                                         || GetLengthInMillis() > 0
                                                         || MediaPlayer.State == VLCState.Error
                                                         || Time.time - startTime > timeoutInSeconds);
    }

    public void Play()
    {
        if (MediaPlayer == null)
        {
            return;
        }

        if (hasReachedEnd
            || MediaPlayer.State is VLCState.Stopped or VLCState.NothingSpecial)
        {
            // libVLC stops at the end of the media. It has to be started again.
            hasReachedEnd = false;
            isStopRequested = false;
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

    public bool IsPlaying => MediaPlayer != null
                             && !hasReachedEnd
                             && MediaPlayer.IsPlaying;

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

            if (hasReachedEnd)
            {
                // libVLC resets the time after the end of the media.
                return DurationInMillis;
            }

            lock (timePointLock)
            {
                if (pendingSeekPositionInMillis.HasValue)
                {
                    return pendingSeekPositionInMillis.Value;
                }

                if (TryInterpolatePositionInMillis(out double interpolatedPositionInMillis))
                {
                    return interpolatedPositionInMillis;
                }
            }

            return Math.Max(0, MediaPlayer.Time / MicrosecondsPerMillisecond);
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

        if (hasReachedEnd)
        {
            // libVLC stops at the end of the media. It has to be started again to be able to seek.
            // The caller decides whether it should be playing afterwards.
            hasReachedEnd = false;
            isStopRequested = false;
            VlcMediaPlayer.Play();
            MediaPlayer.SetPause(true);
        }

        lock (timePointLock)
        {
            pendingSeekPositionInMillis = positionInMillis;
            lastTimePoint = null;
        }

        // Use precise seeking (not fast seeking) because the position is relevant for lyrics and scoring.
        MediaPlayer.SetTime((long)(positionInMillis * MicrosecondsPerMillisecond), false);
    }

    private bool TryInterpolatePositionInMillis(out double positionInMillis)
    {
        positionInMillis = 0;
        if (!lastTimePoint.HasValue
            || VLCMediaPlayer.LibVLC == null)
        {
            return false;
        }

        long systemDate = pausedSystemDateInMicroseconds ?? VLCMediaPlayer.LibVLC.Clock;
        long interpolatedTime = 0;
        double interpolatedPosition = 0;
        if (!MediaPlayer.Interpolate(lastTimePoint.Value, systemDate, ref interpolatedTime, ref interpolatedPosition))
        {
            return false;
        }

        positionInMillis = (double)interpolatedTime / MicrosecondsPerMillisecond;
        return true;
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
        lock (timePointLock)
        {
            lastTimePoint = null;
            pausedSystemDateInMicroseconds = null;
            pendingSeekPositionInMillis = null;
        }
        hasReachedEnd = false;
    }

    // Called from a libVLC thread
    private unsafe void OnTimeUpdate(TimePoint timePoint, void* opaque)
    {
        lock (timePointLock)
        {
            lastTimePoint = timePoint;
            pausedSystemDateInMicroseconds = null;
            pendingSeekPositionInMillis = null;
        }
    }

    // Called from a libVLC thread
    private unsafe void OnTimePaused(long systemDate, void* opaque)
    {
        lock (timePointLock)
        {
            pausedSystemDateInMicroseconds = systemDate;
        }
    }

    // Called from a libVLC thread
    private unsafe void OnTimeSeek(TimePoint? seekRequestValue, void* opaque)
    {
        // Nothing to do. The pending seek position is used until the next time update.
    }

    // Called from a libVLC thread
    private void OnStopped(object sender, EventArgs e)
    {
        if (!isStopRequested
            && DurationInMillis > 0)
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

        if (MediaPlayer != null)
        {
            MediaPlayer.Stopped -= OnStopped;
            MediaPlayer.UnwatchTime();
        }

        VlcMediaPlayer = null;
        if (gameObject != null)
        {
            Object.Destroy(gameObject);
        }
    }
}
