namespace Epub.Core;

public sealed class InvalidEpubException : Exception
{
    public InvalidEpubException(string message) : base(message) { }
    public InvalidEpubException(string message, Exception inner) : base(message, inner) { }
}
