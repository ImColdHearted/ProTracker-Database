using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Foot_Tracker.Services
{
    /// <summary>
    /// Gates the admin-only actions (originally the boss wiki scraper dev
    /// tool, then the Events board tools, since §101 also the Admin Client /
    /// Admin Console - see AdminLoginWindow) behind a single username and
    /// password. This is deliberately NOT a real multi-user auth system -
    /// there's exactly one admin (the developer), this runs entirely locally
    /// with no network round-trip, and the actual goal is just "keep testers
    /// from stumbling into a data-editing tool that isn't meant for them,"
    /// not defending against a determined attacker with the binary in hand.
    ///
    /// §242 moved the credential OUT of this file. It used to hold the
    /// username as a plain constant and the PBKDF2 salt and hash as base64
    /// literals. Never a plaintext password - but the repository went public,
    /// and a salt and hash in a public file is an offline cracking target that
    /// can be worked on forever, by anyone, with no rate limit and no sign
    /// that it is happening. The username sitting beside them narrowed it
    /// further.
    ///
    /// So the credential now lives in a file OUTSIDE the repository, beside
    /// the other per-machine state this app keeps:
    ///
    ///   %LOCALAPPDATA%\ProTracker\Database\admin-credential.json
    ///
    /// created by tools\new-admin-credential.ps1, which is also how the
    /// password is rotated. That file is per-machine and never committed; the
    /// .gitignore names it as well, in case a copy is ever dropped into the
    /// tree by hand.
    ///
    /// What is stored is still only PBKDF2-HMAC-SHA256 over the password, with
    /// its salt and iteration count - never the password itself. Verify
    /// re-derives from what was typed and compares in fixed time.
    ///
    /// It FAILS CLOSED. No file, unreadable file, malformed file, missing
    /// field, short salt or hash, iteration count outside the accepted range:
    /// Verify returns false. There is no built-in fallback credential, no
    /// default password, and no path that grants admin without a match. A
    /// fresh clone therefore has no admin access until the script is run on
    /// that machine, which is the intended behaviour, not a bug.
    /// </summary>
    public static class AdminAuthService
    {
        private static readonly string CredentialPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProTracker", "Database", "admin-credential.json");

        // The generator writes 210,000 - the OWASP-recommended figure for
        // PBKDF2-SHA256 at the time §242 was written, and what the old
        // constants used. A floor is enforced so a hand-edited file cannot
        // quietly weaken the derivation; a ceiling keeps a typo like three
        // extra zeros from freezing the login window instead of failing it.
        private const int MinimumIterations = 100_000;
        private const int MaximumIterations = 5_000_000;
        private const int SaltLengthBytes = 16;
        private const int HashLengthBytes = 32;

        private sealed record Credential(string Username, byte[] Salt, byte[] Hash, int Iterations);

        // Read once. A rotation takes effect on the next run of the app, which
        // is the same as it was when these were compiled-in constants.
        private static readonly Lazy<Credential?> Stored = new(Load);

        /// <summary>
        /// True when this machine has an admin credential at all. The login
        /// window can use it to say "no admin credential on this machine"
        /// instead of "wrong password", which are very different problems.
        /// </summary>
        public static bool IsConfigured => Stored.Value is not null;

        /// <summary>True if the given username/password match the admin credentials.</summary>
        public static bool Verify(string username, string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return false;

            Credential? stored = Stored.Value;
            if (stored is null)
                return false;

            // The username isn't a secret, but it is not in the binary any
            // more either, so it is compared the same way as before: exactly,
            // and case-sensitively, because there is no reason to be lenient.
            if (!string.Equals(username, stored.Username, StringComparison.Ordinal))
                return false;

            byte[] candidateHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                stored.Salt,
                stored.Iterations,
                HashAlgorithmName.SHA256,
                stored.Hash.Length);

            // Fixed-time comparison - there's no real threat model here that
            // needs it (this never crosses a network), but it costs nothing and
            // is the standard-practice way to compare secrets/hashes.
            return CryptographicOperations.FixedTimeEquals(candidateHash, stored.Hash);
        }

        /// <summary>
        /// Reads the credential file. Returns null for every failure, and says
        /// nothing about which one: a missing file and a corrupt file are the
        /// same answer to a caller, and the difference is not worth writing
        /// somewhere a support bundle might pick it up.
        /// </summary>
        private static Credential? Load()
        {
            try
            {
                if (!File.Exists(CredentialPath))
                    return null;

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CredentialPath));
                JsonElement root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return null;

                string? username = ReadString(root, "username");
                byte[]? salt = ReadBase64(root, "salt");
                byte[]? hash = ReadBase64(root, "hash");

                if (string.IsNullOrEmpty(username) || salt is null || hash is null)
                    return null;

                // A short salt or a short hash is a broken file, not a weaker
                // credential to be accepted anyway.
                if (salt.Length < SaltLengthBytes || hash.Length < HashLengthBytes)
                    return null;

                if (!root.TryGetProperty("iterations", out JsonElement iterationsElement) ||
                    iterationsElement.ValueKind != JsonValueKind.Number ||
                    !iterationsElement.TryGetInt32(out int iterations))
                    return null;

                if (iterations < MinimumIterations || iterations > MaximumIterations)
                    return null;

                return new Credential(username, salt, hash, iterations);
            }
            catch (Exception)
            {
                // Unreadable, locked, truncated, not JSON at all: no credential.
                return null;
            }
        }

        private static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static byte[]? ReadBase64(JsonElement root, string name)
        {
            string? raw = ReadString(root, name);
            if (string.IsNullOrEmpty(raw))
                return null;

            try
            {
                return Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
