namespace NettoMarkdowns.Services;

/// <summary>An error we want to show the user verbatim, rather than a stack trace.</summary>
public sealed class SallingException(string message, Exception? inner = null)
    : Exception(message, inner);
