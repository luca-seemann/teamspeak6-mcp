using System.Runtime.ExceptionServices;

namespace TeamSpeak.Query.Client;

/// <summary>
/// Reports what went wrong while closing several resources, after every one of them has been tried.
/// </summary>
internal static class DisposalFailures
{
    /// <summary>Rethrows the failures collected while closing, if there were any.</summary>
    /// <param name="failures">The exceptions caught, or <see langword="null"/> when none were.</param>
    /// <param name="message">The message for an <see cref="AggregateException"/> when there were several.</param>
    /// <remarks>A single failure is rethrown as it was, with its original stack trace.</remarks>
    public static void ThrowIfAny(List<Exception>? failures, string message)
    {
        switch (failures)
        {
            case null or []:
                return;

            case [var single]:
                ExceptionDispatchInfo.Throw(single);
                return;

            default:
                throw new AggregateException(message, failures);
        }
    }
}