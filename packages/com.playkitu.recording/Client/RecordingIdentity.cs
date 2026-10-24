using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Playkitu.Client
{
    // Shared source, also compiled by the issuer. Keep the identity byte encoding
    // independent of Unity/.NET10 serializers and client wall-clock formatting.
    public static class RecordingIdentity
    {
        public const string Prefix = "pkrec2:";
        public const int IdentityLength = 29;
        public static string NewProof()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Base64Url(bytes);
        }
        public static string Identity(Guid sessionId, long unixSeconds, string proof, string prefix = Prefix, string domain = "playkitu-recording:")
        {
            using (var sha = SHA256.Create())
            {
                // Bind the complete admission tuple, including the timestamp, to a
                // domain-separated 128-bit digest. Proof contributes its canonical
                // Base64URL TEXT, not decoded bytes. Validation rejects proof aliases
                // before admission. This compact Steam compatibility candidate has
                // 128-bit targeted resistance / 64-bit generic collision resistance;
                // the independent bearer proof retains its full 256-bit entropy.
                var canonical = domain + prefix + sessionId.ToString("N")
                    + ":" + unixSeconds.ToString(CultureInfo.InvariantCulture) + ":" + proof;
                var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var compact = new byte[16];
                Buffer.BlockCopy(digest, 0, compact, 0, compact.Length);
                return prefix + Base64Url(compact);
            }
        }
        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public static bool ValidProof(string value)
        {
            if (value == null || value.Length != 43) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
            try { var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "="); return bytes.Length == 32 && Base64Url(bytes) == value; }
            catch (FormatException) { return false; }
        }
    }
}
