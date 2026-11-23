/**
 * Marker for the libVLC based video support provider that shows the video of the libVLC media player
 * that is already used by the SongAudioPlayer (i.e. the audio file is also the video file).
 * The implementation lives in an optional assembly that is only compiled when the VLC for Unity plugin is installed.
 */
public interface ISongAudioPlayerVlcVideoSupportProvider : IVideoSupportProvider
{
}
