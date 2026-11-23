using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UniInject;
using UnityEngine;
using UnityEngine.TestTools;

// Checks whether the AI tools work with the models that are available locally.
// The models are not part of the repository (see Assets/StreamingAssets/AiModels and sherpa-onnx), so the test runs only on request.
[Explicit("Requires locally installed AI models")]
public class AiModelsSmokeTest : AbstractPlayModeTest
{
    private static string WorkFolderPath => $"{Application.temporaryCachePath}/{nameof(AiModelsSmokeTest)}";

    [Inject]
    private AudioSeparationManager audioSeparationManager;

    [Inject]
    private SpeechRecognitionManager speechRecognitionManager;

    [Inject]
    private PitchDetectionManager pitchDetectionManager;

    [UnityTest]
    public IEnumerator A_PitchDetection() => RunAsync("PitchDetection", async songMeta =>
    {
        PitchDetectionResult result = await pitchDetectionManager.ProcessSongMetaJob(songMeta).GetResultAsync();
        return $"{result.Notes.Count} notes, midi {string.Join(",", result.Notes.Take(10).Select(note => note.MidiNote))}";
    });

    [UnityTest]
    public IEnumerator B_SpeechRecognition() => RunAsync("SpeechRecognition", async songMeta =>
    {
        AudioClip audioClip = await AudioSampleLoader.Instance.LoadAsAudioClip(SongMetaUtils.GetAudioUri(songMeta));
        float[] samples = AudioSampleUtils.GetAudioSamples(audioClip, 0);
        SpeechRecognitionResult result = await speechRecognitionManager
            .ProcessSongMetaJob(new SpeechRecognitionInputSamples(samples, 0, samples.Length - 1, audioClip.frequency))
            .GetResultAsync();
        return $"text '{result.Text}', {result.Words?.Count} words";
    });

    [UnityTest]
    public IEnumerator C_AudioSeparation() => RunAsync("AudioSeparation", async songMeta =>
    {
        AudioSeparationResult result = await audioSeparationManager.ProcessSongMetaJob(songMeta, false).GetResultAsync();
        return $"{result}, vocals exists {File.Exists(result.VocalsAudioPath)}, instrumental exists {File.Exists(result.InstrumentalAudioPath)}";
    });

    private IEnumerator RunAsync(string toolName, Func<SongMeta, Awaitable<string>> action)
    {
        return RunInternalAsync(toolName, action);
    }

    private async Awaitable RunInternalAsync(string toolName, Func<SongMeta, Awaitable<string>> action)
    {
        LogAssertUtils.IgnoreFailingMessages();
        DirectoryUtils.Delete(WorkFolderPath, true);
        Directory.CreateDirectory(WorkFolderPath);
        string audioFilePath = $"{WorkFolderPath}/audio.ogg";
        File.Copy($"{Application.dataPath}/Tests/PlayModeTests/ForcedAlignmentTest/ForcedAlignment-UmlautsTest.ogg", audioFilePath);

        SongMeta songMeta = new UltraStarSongMeta("TestArtist", "TestTitle", 300, audioFilePath, new Dictionary<EVoiceId, string>());
        songMeta.SetFileInfo($"{WorkFolderPath}/song.txt");
        // Pitch detection runs on the isolated vocals. The test audio is a cappella anyway.
        songMeta.VocalsAudio = "audio.ogg";

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string summary = await action(songMeta);
            Debug.Log($"AISMOKE {toolName}: OK after {stopwatch.ElapsedMilliseconds} ms: {summary}");
        }
        catch (Exception ex)
        {
            Debug.Log($"AISMOKE {toolName}: FAILED after {stopwatch.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message} | inner: {ex.InnerException?.Message}");
            Assert.Fail($"{toolName}: {ex.Message}");
        }
    }
}
