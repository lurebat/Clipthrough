using System;

namespace Clipthrough.Services;

/// <summary>
/// The chosen data folder cannot be used. The message is written for the user:
/// Program shows it and exits rather than falling back to another folder.
/// </summary>
public sealed class AppDataRootException : Exception
{
    public AppDataRootException()
    {
    }

    public AppDataRootException(string message)
        : base(message)
    {
    }

    public AppDataRootException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
