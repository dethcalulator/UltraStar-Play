using System;

public class VlcPlaybackException : Exception
{
    public VlcPlaybackException(string message)
        : base(message)
    {
    }
}
