using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Penumbra
{
    /// <summary>Finds a sharp (1000 px) cover for a song with Apple's free iTunes search, because Windows
    /// only gets a tiny one from the browser (120 px from YouTube Music). This sends the song's title and
    /// artist to itunes.apple.com, so it's only used while the Now playing screen is open.</summary>
    public static class CoverLookup
    {
        static readonly HttpClient Http = MakeClient();

        static HttpClient MakeClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Penumbra/0.1 (personal ambient light project)");
            return h;
        }

        /// <summary>The JPEG bytes of the cover, or null if nothing matching was found (never throws).</summary>
        public static async Task<byte[]?> FindAsync(string artist, string title, CancellationToken ct)
        {
            try
            {
                string a = Artist(artist), t = Bare(title);
                // a music video: "Maroon 5 - Payphone (Official Video)" by "Maroon5VEVO"
                int dash = t.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0 && Similar(t[..dash], a)) t = t[(dash + 3)..].Trim();
                if (a.Length == 0 || t.Length == 0) return null;

                string url = "https://itunes.apple.com/search?entity=song&limit=10&term=" + Uri.EscapeDataString($"{a} {t}");
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
                foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    // only a result by the same artist with the same title: a wrong cover is worse than a blurry one
                    string ra = r.TryGetProperty("artistName", out var x) ? x.GetString() ?? "" : "";
                    string rt = r.TryGetProperty("trackName", out var y) ? y.GetString() ?? "" : "";
                    if (!SameArtist(ra, a) || !Similar(Bare(rt), t)) continue;
                    if (!r.TryGetProperty("artworkUrl100", out var art) || art.GetString() is not string small) continue;
                    return await Http.GetByteArrayAsync(small.Replace("100x100bb", "1000x1000bb"), ct);
                }
            }
            catch { /* offline, timed out, odd reply: the small cover will do */ }
            return null;
        }

        // the same clean-ups as engine/lyrics.py, so YouTube's names match the shop's
        static string Clean(string title)
        {
            title = Regex.Replace(title, @"\s*[\(\[].*?(official|video|audio|lyrics?|remaster|visualizer).*?[\)\]]", "", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\s+-\s+.*remaster.*$", "", RegexOptions.IgnoreCase);
            return title.Trim();
        }

        static string Bare(string title) => Regex.Replace(Clean(title), @"\s*[\(\[].*?[\)\]]", "").Trim();

        static string Artist(string artist)
        {
            var a = Regex.Replace(artist ?? "", @"\s+-\s+Topic$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(a, "VEVO$", "").Trim();
        }

        /// <summary>Letters and digits only, lower case ("Maroon 5" and "Maroon5" are the same).</summary>
        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        static bool Similar(string a, string b)
        {
            string x = Norm(a), y = Norm(b);
            return x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));
        }

        /// <summary>One of the artists matches ("Bruno Mars, Anderson .Paak" vs "Silk Sonic, Bruno Mars & Anderson .Paak").</summary>
        static bool SameArtist(string found, string wanted)
        {
            var parts = Regex.Split(wanted, @"\s*(?:,|&|\bfeat\.?|\bft\.?|\bx\b)\s*", RegexOptions.IgnoreCase).Where(p => p.Length > 1);
            return Similar(found, wanted) || parts.Any(p => Similar(found, p));
        }
    }
}
