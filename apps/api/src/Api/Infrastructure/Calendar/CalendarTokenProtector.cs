using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Clinic.Api.Infrastructure.Calendar;

/// <summary>
/// Seals a professional's refresh token so what lands in the database is not the credential
/// (design K3). The one piece of this change that <c>03-nfr.md</c> §2 has been asking for
/// since planning.
/// </summary>
/// <remarks>
/// <para>
/// <b>AES-256-GCM under a key from configuration.</b> The obvious framework answer is ASP.NET
/// Core Data Protection, and it was rejected on its key ring's lifecycle: by default that ring
/// is written to the container filesystem, which Compose recreates, so every <c>down</c>/<c>up</c>
/// would leave stored tokens nothing can read and professionals silently needing to reconnect.
/// Persisting the ring properly means another package, another table, and a second
/// key-management story. For <b>one secret with one lifetime</b>, an explicit key is less
/// machinery and far more legible — you can point at the value that protects the token.
/// </para>
/// <para>
/// <b>GCM rather than CBC + HMAC</b> because it authenticates in one primitive. The failure mode
/// of forgetting the authentication half is silent, and a silently unauthenticated ciphertext is
/// how a tampered token becomes a request to somebody else's calendar.
/// </para>
/// <para>
/// <b>The envelope carries its version.</b> Rotation is not in scope, but a stored blob with no
/// room to say how it was made can only be rotated by a migration that decrypts everything with
/// the one key it can no longer assume is right. Four characters today; the difference between
/// rotation being additive and rotation being an incident.
/// </para>
/// <para>
/// <b>Nothing here logs.</b> The methods take and return strings and say nothing about them; the
/// call sites log the outcome, never the material. That is a property a later helpful log line
/// would quietly break, which is why it is written down rather than merely true.
/// </para>
/// </remarks>
internal sealed class CalendarTokenProtector
{
    /// <summary>The scheme this class writes. Read back before anything is decrypted.</summary>
    private const string Version = "v1";

    /// <summary>Separates the envelope's three parts. Not present in base64url output.</summary>
    private const char Separator = '.';

    private const int NonceByteLength = 12;
    private const int TagByteLength = 16;

    private readonly Lazy<byte[]> key;

    public CalendarTokenProtector(IOptions<CalendarOptions> options)
    {
        // Resolved on FIRST USE rather than in the constructor, and that is not a micro-
        // optimisation. This type is a dependency of CalendarWithdrawal, which is on the account
        // DISABLE path — a path that must keep working on a deployment with no calendar
        // configured at all, which is a supported deployment (design K4). Throwing here would
        // mean turning off a staff account failed because the clinic never connected a calendar.
        //
        // Deferring costs nothing in safety: with the feature off, no credential can ever have
        // been stored, so nothing reaches Seal or Open. If something does, it throws then, and
        // the message says exactly what is missing.
        key = new Lazy<byte[]>(() =>
        {
            var configured = options.Value.TokenEncryptionKey;

            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new InvalidOperationException(
                    "Calendar__TokenEncryptionKey is not configured, so no token can be protected. " +
                    "Nothing should be sealing or opening a credential when the calendar feature is off.");
            }

            var bytes = Convert.FromBase64String(configured);

            if (bytes.Length != CalendarOptionsValidator.KeyByteLength)
            {
                throw new InvalidOperationException(
                    $"Calendar__TokenEncryptionKey decodes to {bytes.Length} bytes after validation " +
                    $"passed; expected {CalendarOptionsValidator.KeyByteLength}.");
            }

            return bytes;
        });
    }

    /// <summary>
    /// Seals a refresh token into the value that is safe to store.
    /// </summary>
    /// <remarks>
    /// A fresh nonce per operation, which is why sealing the same token twice produces two
    /// different envelopes. That is the property being relied on, not an accident: reusing a
    /// nonce under one key is the way GCM fails catastrophically.
    /// </remarks>
    public string Seal(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(NonceByteLength);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagByteLength];

        using var aes = new AesGcm(key.Value, TagByteLength);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Tag appended to the ciphertext rather than given its own field: it is fixed-length and
        // always travels with it, so a third part would be a third thing to get wrong.
        var sealedBytes = new byte[cipherBytes.Length + TagByteLength];
        cipherBytes.CopyTo(sealedBytes, 0);
        tag.CopyTo(sealedBytes, cipherBytes.Length);

        return string.Join(Separator, Version, Encode(nonce), Encode(sealedBytes));
    }

    /// <summary>
    /// Opens a sealed value.
    /// </summary>
    /// <exception cref="CalendarTokenProtectionException">
    /// The envelope is malformed, carries an unknown version, or fails authentication — which
    /// includes the case that matters operationally: <b>the key changed</b>.
    /// </exception>
    public string Open(string sealedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sealedValue);

        var parts = sealedValue.Split(Separator);

        if (parts.Length != 3)
        {
            throw new CalendarTokenProtectionException(
                "The stored calendar credential is not a sealed envelope.");
        }

        if (!string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            // Named explicitly, because the day this fires is the day somebody is mid-rotation
            // and needs to know which scheme they are looking at.
            throw new CalendarTokenProtectionException(
                $"The stored calendar credential uses scheme '{parts[0]}', which this build " +
                $"cannot read (it writes '{Version}').");
        }

        byte[] nonce;
        byte[] sealedBytes;

        try
        {
            nonce = Decode(parts[1]);
            sealedBytes = Decode(parts[2]);
        }
        catch (FormatException)
        {
            throw new CalendarTokenProtectionException(
                "The stored calendar credential is not decodable.");
        }

        if (nonce.Length != NonceByteLength || sealedBytes.Length < TagByteLength)
        {
            throw new CalendarTokenProtectionException(
                "The stored calendar credential has the wrong shape.");
        }

        var cipherLength = sealedBytes.Length - TagByteLength;
        var cipherBytes = sealedBytes.AsSpan(0, cipherLength);
        var tag = sealedBytes.AsSpan(cipherLength);
        var plainBytes = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(key.Value, TagByteLength);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        catch (CryptographicException)
        {
            // Tampering and a changed key are indistinguishable here, and deliberately reported
            // as one thing: both mean this credential can no longer be used, and the remedy for
            // both is the professional reconnecting.
            throw new CalendarTokenProtectionException(
                "The stored calendar credential could not be opened. Either it was altered or " +
                "Calendar__TokenEncryptionKey has changed since it was stored.");
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);

        return Convert.FromBase64String(padded);
    }
}

/// <summary>
/// A sealed calendar credential could not be opened.
/// </summary>
/// <remarks>
/// Its own type rather than a bare <see cref="CryptographicException"/>, so the slice can catch
/// exactly this and report a connection that needs re-establishing — instead of letting a
/// key-rotation accident surface to a professional as an unhandled server error.
/// </remarks>
internal sealed class CalendarTokenProtectionException(string message) : Exception(message);
