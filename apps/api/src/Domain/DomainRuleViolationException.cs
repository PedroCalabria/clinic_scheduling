namespace Clinic.Domain;

/// <summary>
/// Thrown when a caller asks the protected core to do something its rules forbid.
/// </summary>
/// <remarks>
/// A distinct type rather than <see cref="InvalidOperationException"/> so the API can tell
/// "the domain refused" apart from "something unexpected broke" and answer with a catalogue
/// code instead of <c>server.unexpected</c> (docs/07-error-codes.md).
///
/// The message is for logs and developers only — it is never returned to a caller, because
/// the API returns codes, never prose (Decision I).
/// </remarks>
public sealed class DomainRuleViolationException(string message) : Exception(message);
