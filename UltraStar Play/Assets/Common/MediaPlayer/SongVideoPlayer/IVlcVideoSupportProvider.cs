/**
 * Marker for the libVLC based video support provider that uses its own libVLC media player.
 * The implementation lives in an optional assembly that is only compiled when the VLC for Unity plugin is installed.
 */
public interface IVlcVideoSupportProvider : IVideoSupportProvider
{
}
