using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/**
 * Tests the libVLC based audio and video support providers.
 * Only compiled when the VLC for Unity plugin is installed.
 */
public class VlcMediaFileFormatTest : AbstractMediaFileFormatTest
{
    private const double MaxAudioVideoOffsetInMillis = 150;

    // Formats that Unity cannot play (on any platform or at least on Linux), such that VLC is used as fallback.
    private static readonly List<TestCaseData> vlcAsFallbackAudio = new()
    {
        new TestCaseData("m4a.txt").Returns(null),
        new TestCaseData("aac.txt").Returns(null),
        new TestCaseData("flac.txt").Returns(null),
        new TestCaseData("mkv.txt").Returns(null),
        new TestCaseData("mov.txt").Returns(null),
#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
        new TestCaseData("mp4.txt").Returns(null),
#endif
    };

    // Formats that are only handled by VLC when the Unity API is disabled.
    private static readonly List<TestCaseData> vlcOnlyAudio = new()
    {
        new TestCaseData("mp3-ConstantBitRate.txt").Returns(null),
        new TestCaseData("mp3-VariableBitRate.txt").Returns(null),
        new TestCaseData("ogg.txt").Returns(null),
        new TestCaseData("wav.txt").Returns(null),
        new TestCaseData("mp4.txt").Returns(null),
        new TestCaseData("webm-vp8.txt").Returns(null),
        new TestCaseData("webm-vp9.txt").Returns(null),
    };

    [UnityTest]
    [TestCaseSource(nameof(vlcAsFallbackAudio))]
    public IEnumerator ShouldLoadAudioViaVlcAsFallback(string txtFilePath)
    {
        ConfigureMediaApiSettings(EApiUsage.Enabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync(txtFilePath, typeof(VlcAudioSupportProvider));
    }

    [UnityTest]
    [TestCaseSource(nameof(vlcOnlyAudio))]
    public IEnumerator ShouldLoadAudioViaVlc(string txtFilePath)
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync(txtFilePath, typeof(VlcAudioSupportProvider));
    }

    [UnityTest]
    public IEnumerator ShouldPreferUnityForMp3AndOgg()
    {
        ConfigureMediaApiSettings(EApiUsage.Enabled, EApiUsage.Preferred);
        yield return SongAudioPlayerShouldLoadFileAsync("mp3-VariableBitRate.txt", typeof(AudioSourceAudioSupportProvider));
    }

    [UnityTest]
    public IEnumerator ShouldShowVideoOfSongAudioPlayerWhenVideoEqualsAudio()
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongVideoPlayerShouldLoadFileAsync("mp4.txt", typeof(SongAudioPlayerVlcVideoSupportProvider));
    }

    [UnityTest]
    public IEnumerator ShouldLoadSeparateVideoViaVlc()
    {
        ConfigureMediaApiSettings(EApiUsage.Enabled, EApiUsage.Preferred);
        yield return SongVideoPlayerShouldLoadFileAsync("mp4-video-ogg-audio.txt", typeof(VlcVideoSupportProvider));
    }

    [UnityTest]
    public IEnumerator ShouldStartAtStartTagPosition()
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync("ogg-WithStartTag.txt", typeof(VlcAudioSupportProvider));
        yield return new WaitForSeconds(0.1f);
        Assert.That(songAudioPlayer.PositionInMillis, Is.GreaterThanOrEqualTo(2000));
    }

    [UnityTest]
    public IEnumerator ShouldReturnSetPositionImmediately()
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync("m4a.txt", typeof(VlcAudioSupportProvider));

        // SongAudioPlayer reads the position right after setting it to detect jumps (e.g. "skip to next lyrics").
        songAudioPlayer.PositionInMillis = 3000;
        Assert.That(songAudioPlayer.PositionInMillisExact, Is.EqualTo(3000).Within(50));

        songAudioPlayer.PositionInMillis = 0;
        Assert.That(songAudioPlayer.PositionInMillisExact, Is.LessThan(50));
    }

    [UnityTest]
    public IEnumerator ShouldPauseAndResume()
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync("m4a.txt", typeof(VlcAudioSupportProvider));
        yield return new WaitForSeconds(0.5f);

        songAudioPlayer.PauseAudio();
        yield return new WaitForSeconds(0.2f);
        double pausedPositionInMillis = songAudioPlayer.PositionInMillisExact;
        yield return new WaitForSeconds(0.5f);
        Assert.IsFalse(songAudioPlayer.IsPlayingOfAudioProvider, "Should not resume playback by itself");
        Assert.That(songAudioPlayer.PositionInMillisExact, Is.EqualTo(pausedPositionInMillis).Within(20));

        songAudioPlayer.PlayAudio();
        yield return new WaitForSeconds(0.5f);
        Assert.IsTrue(songAudioPlayer.IsPlayingOfAudioProvider);
        Assert.That(songAudioPlayer.PositionInMillisExact, Is.GreaterThan(pausedPositionInMillis + 300));
    }

    [UnityTest]
    public IEnumerator PositionShouldIncreaseSmoothly()
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync("m4a.txt", typeof(VlcAudioSupportProvider));
        yield return new WaitForSeconds(0.3f);

        double lastPositionInMillis = songAudioPlayer.PositionInMillisExact;
        for (int i = 0; i < 60; i++)
        {
            yield return null;
            double positionInMillis = songAudioPlayer.PositionInMillisExact;
            Assert.That(positionInMillis, Is.GreaterThanOrEqualTo(lastPositionInMillis), "Position should never decrease during playback");
            lastPositionInMillis = positionInMillis;
        }
    }

    [UnityTest]
    public IEnumerator ShouldApplyVolume()
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync("m4a.txt", typeof(VlcAudioSupportProvider));

        songAudioPlayer.VolumeFactor = 0.5f;
        yield return new WaitForSeconds(0.2f);
        Assert.That(songAudioPlayer.CurrentAudioSupportProvider.VolumeFactor, Is.EqualTo(0.5).Within(0.01));
    }

    [UnityTest]
    public IEnumerator VideoShouldStayInSyncAfterJump()
    {
        ConfigureMediaApiSettings(EApiUsage.Enabled, EApiUsage.Preferred);
        yield return SongVideoPlayerShouldLoadFileAsync("mp4-video-ogg-audio.txt", typeof(VlcVideoSupportProvider));
        yield return new WaitForSeconds(0.5f);

        // Jump forward like "skip to next lyrics", then backward.
        foreach (double targetPositionInMillis in new double[] { 3000, 500 })
        {
            songAudioPlayer.PositionInMillis = targetPositionInMillis;
            yield return new WaitForSeconds(0.5f);

            double offsetInMillis = songVideoPlayer.PositionInMillis - songAudioPlayer.PositionInMillis;
            Debug.Log($"Audio/video offset after jump to {targetPositionInMillis} ms: {offsetInMillis:F1} ms");
            Assert.That(Math.Abs(offsetInMillis), Is.LessThan(MaxAudioVideoOffsetInMillis),
                $"Video should be in sync with audio after jump to {targetPositionInMillis} ms");
        }
    }

    [UnityTest]
    public IEnumerator ShouldDisposeVlcMediaPlayerOnUnload()
    {
        ConfigureMediaApiSettings(EApiUsage.Disabled, EApiUsage.Enabled);
        yield return SongAudioPlayerShouldLoadFileAsync("m4a.txt", typeof(VlcAudioSupportProvider));
        VlcAudioSupportProvider provider = songAudioPlayer.CurrentAudioSupportProvider as VlcAudioSupportProvider;
        Assert.NotNull(provider?.VlcPlayback);

        songAudioPlayer.UnloadAudio();
        yield return null;
        Assert.IsNull(provider.VlcPlayback);
        Assert.AreEqual(0, provider.GetComponentsInChildren<LibVLCSharp.VLCMediaPlayer>().Length);
    }

    private static void ConfigureMediaApiSettings(EApiUsage unity, EApiUsage vlc)
    {
        SettingsManager.Instance.Settings.UnityMediaApiUsage = unity;
        SettingsManager.Instance.Settings.AvProApiUsage = EApiUsage.Disabled;
        SettingsManager.Instance.Settings.VlcApiUsage = vlc;
    }
}
