using System.Security.Cryptography;
using System.Text;

namespace VectorNNTPD.TransitService.Session.Commands.Posting
{
    /// <summary>Synthesizes a POST Message-ID as <c>&lt;MD5(UUID())@usenet.ninja&gt;</c>.</summary>
    internal static class PostMessageIdFactory
    {
        /// <summary>Right-hand side of a synthesized Message-ID.</summary>
        public const string Domain = "usenet.ninja";

        /// <summary>
        /// Creates a Message-ID for one posting attempt. The UUID is generated once;
        /// MD5 is taken over its canonical <c>D</c>-format text (not the raw UUID).
        /// </summary>
        public static string Create()
        {
            var uuid = Guid.NewGuid().ToString("D");
            var hash = MD5.HashData(Encoding.ASCII.GetBytes(uuid));
            var hex = Convert.ToHexStringLower(hash);
            return string.Create(hex.Length + Domain.Length + 3, hex, static (span, id) =>
            {
                span[0] = '<';
                id.CopyTo(span[1..]);
                span[1 + id.Length] = '@';
                Domain.CopyTo(span[(2 + id.Length)..]);
                span[^1] = '>';
            });
        }
    }
}
