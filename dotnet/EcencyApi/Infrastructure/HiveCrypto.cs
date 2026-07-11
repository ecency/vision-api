using System.Text;
using NBitcoin.Secp256k1;
using Org.BouncyCastle.Crypto.Digests;
using SHA256 = System.Security.Cryptography.SHA256;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Port of the @hiveio/dhive crypto the Node service uses: key-from-login
/// derivation, Graphene-canonical ECDSA signing (with dhive's exact
/// deterministic-nonce retry loop, so signatures are byte-identical), and
/// public-key recovery for HiveSigner code validation. Verified against
/// dhive-generated golden vectors in EcencyApi.Tests.
/// </summary>
public static class HiveCrypto
{
    private const string PublicKeyPrefix = "STM";
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    // ---- key derivation -------------------------------------------------

    /// <summary>dhive PrivateKey.fromLogin: sha256(username + role + password).</summary>
    public static ECPrivKey FromLogin(string username, string password, string role = "posting")
    {
        var seed = username + role + password;
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return ECPrivKey.Create(secret);
    }

    /// <summary>dhive PrivateKey.toString(): WIF (0x80 || key || dsha256 checksum).</summary>
    public static string ToWif(ECPrivKey key)
    {
        Span<byte> secret = stackalloc byte[32];
        key.WriteToSpan(secret);

        var payload = new byte[33];
        payload[0] = 0x80;
        secret.CopyTo(payload.AsSpan(1));

        var checksum = SHA256.HashData(SHA256.HashData(payload));
        var full = new byte[37];
        payload.CopyTo(full, 0);
        Array.Copy(checksum, 0, full, 33, 4);

        return Base58Encode(full);
    }

    /// <summary>dhive PublicKey.toString(): STM + base58(pub33 || ripemd160(pub33)[0..4]).</summary>
    public static string PublicKeyToString(ECPubKey pubKey)
    {
        Span<byte> compressed = stackalloc byte[33];
        pubKey.WriteToSpan(true, compressed, out _);

        var checksum = Ripemd160(compressed);
        var full = new byte[37];
        compressed.CopyTo(full);
        Array.Copy(checksum, 0, full, 33, 4);

        return PublicKeyPrefix + Base58Encode(full);
    }

    public static string PublicKeyFromLogin(string username, string password, string role = "posting")
        => PublicKeyToString(FromLogin(username, password, role).CreatePubKey());

    // ---- signing --------------------------------------------------------

    /// <summary>
    /// dhive PrivateKey.sign(digest): RFC6979 deterministic ECDSA with extra
    /// entropy sha256(digest || attemptByte), retried until the signature is
    /// Graphene-canonical. Returns the 65-byte hex string ((recid+31) || r || s)
    /// that dhive's Signature.toString() produces.
    /// </summary>
    public static string Sign(ECPrivKey key, ReadOnlySpan<byte> digest32)
    {
        if (digest32.Length != 32)
        {
            throw new ArgumentException("digest must be 32 bytes", nameof(digest32));
        }

        var digestArr = digest32.ToArray();
        Span<byte> compact = stackalloc byte[64];
        var attempts = 0;

        while (true)
        {
            attempts++;
            if (attempts > 255)
            {
                throw new InvalidOperationException("could not produce canonical signature");
            }

            // dhive: options.data = sha256(Buffer.concat([digest, Buffer.alloc(1, attempts)]))
            var ndataInput = new byte[33];
            digestArr.CopyTo(ndataInput, 0);
            ndataInput[32] = (byte)attempts;
            var extraEntropy = SHA256.HashData(ndataInput);

            if (!key.TrySignECDSA(digestArr, new RFC6979NonceFunction(extraEntropy),
                    out var recid, out var sig) || sig == null)
            {
                continue;
            }

            sig.WriteCompactToSpan(compact);

            if (IsCanonical(compact))
            {
                var result = new byte[65];
                result[0] = (byte)(recid + 31);
                compact.CopyTo(result.AsSpan(1));
                return Convert.ToHexStringLower(result);
            }
        }
    }

    /// <summary>Graphene canonical-signature check (dhive isCanonicalSignature).</summary>
    private static bool IsCanonical(ReadOnlySpan<byte> c) =>
        (c[0] & 0x80) == 0
        && !(c[0] == 0 && (c[1] & 0x80) == 0)
        && (c[32] & 0x80) == 0
        && !(c[32] == 0 && (c[33] & 0x80) == 0);

    // ---- recovery -------------------------------------------------------

    /// <summary>
    /// dhive Signature.fromString(sig).recover(digest).toString(): recover the
    /// signer's public key from a 65-byte hex signature. Returns null when the
    /// signature is malformed or recovery fails (callers treat that as invalid).
    /// </summary>
    public static string? RecoverPublicKey(string signatureHex, ReadOnlySpan<byte> digest32)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromHexString(signatureHex);
        }
        catch (FormatException)
        {
            return null;
        }

        if (raw.Length != 65 || digest32.Length != 32)
        {
            return null;
        }

        var recid = raw[0] - 31;
        if (recid < 0 || recid > 3)
        {
            return null;
        }

        if (!SecpRecoverableECDSASignature.TryCreateFromCompact(
                raw.AsSpan(1), recid, out var recSig) || recSig == null)
        {
            return null;
        }

        if (!ECPubKey.TryRecover(Context.Instance, recSig, digest32, out var pubKey) || pubKey == null)
        {
            return null;
        }

        return PublicKeyToString(pubKey);
    }

    // ---- hashing / encoding ----------------------------------------------

    public static byte[] Sha256Utf8(string message) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(message));

    private static byte[] Ripemd160(ReadOnlySpan<byte> data)
    {
        var digest = new RipeMD160Digest();
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var output = new byte[20];
        digest.DoFinal(output, 0);
        return output;
    }

    public static string Base58Encode(ReadOnlySpan<byte> data)
    {
        // count leading zeros
        var zeros = 0;
        while (zeros < data.Length && data[zeros] == 0)
        {
            zeros++;
        }

        var input = data.ToArray();
        var encoded = new char[data.Length * 2]; // plenty
        var outputStart = encoded.Length;

        for (var inputStart = zeros; inputStart < input.Length;)
        {
            encoded[--outputStart] = Base58Alphabet[DivMod(input, inputStart, 256, 58)];
            if (input[inputStart] == 0)
            {
                inputStart++;
            }
        }

        while (outputStart < encoded.Length && encoded[outputStart] == Base58Alphabet[0])
        {
            outputStart++;
        }

        for (; zeros > 0; zeros--)
        {
            encoded[--outputStart] = Base58Alphabet[0];
        }

        return new string(encoded, outputStart, encoded.Length - outputStart);
    }

    private static byte DivMod(byte[] number, int firstDigit, int baseIn, int baseOut)
    {
        var remainder = 0;
        for (var i = firstDigit; i < number.Length; i++)
        {
            var digit = number[i] & 0xFF;
            var temp = remainder * baseIn + digit;
            number[i] = (byte)(temp / baseOut);
            remainder = temp % baseOut;
        }
        return (byte)remainder;
    }
}
