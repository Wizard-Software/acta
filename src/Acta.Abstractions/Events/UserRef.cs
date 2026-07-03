namespace Acta.Abstractions;

/// <summary>
/// Pseudonymous technical identifier of a user (ADR-017). The value MUST be a technical
/// identifier — e.g., an account GUID or an identity-provider subject id — and MUST NEVER be
/// an e-mail address, login, display name, or any other piece of personal data.
/// <see cref="EventMetadata"/> lives in an append-only store outside the reach of Forgettable
/// Payloads (ADR-008); raw PII written there would be unerasable. The pseudonym-to-identity
/// mapping is maintained by the HOST, outside Acta, in a store covered by its own erasure path.
/// The constructor applies only a heuristic, fail-fast safety net (rejects <see langword="null"/>,
/// values containing '@', and values longer than 128 characters) — full PII classification
/// remains the host's responsibility.
/// </summary>
public readonly record struct UserRef
{
    private const string ValidationMessage =
        "UserRef must be a technical pseudonym: it must not be null, must not contain '@', and must be 128 characters or fewer.";

    /// <summary>The underlying pseudonym value.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new <see cref="UserRef"/>, applying the heuristic fail-fast validation from
    /// ADR-017.
    /// </summary>
    /// <param name="value">The technical pseudonym (e.g., an account GUID or IdP subject id).</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="value"/> is <see langword="null"/>, contains '@', or is
    /// longer than 128 characters. The message never echoes <paramref name="value"/> — a
    /// rejected value is likely PII, and echoing it into logs would defeat the purpose of this
    /// type (SEC-1).
    /// </exception>
    public UserRef(string value)
    {
        if (value is null || value.Contains('@') || value.Length > 128)
        {
            throw new ArgumentException(ValidationMessage, nameof(value));
        }

        Value = value;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}
