using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace BigWalkVRInstaller.Services
{
    public static class RepoClient
    {
        static readonly HttpClient Http;
        // unpublished builds for local testing, packages in it may be file URLs
        public static readonly string LocalManifest = Environment.GetEnvironmentVariable("CIRCUITLORD_LOCAL_MANIFEST");

        static RepoClient()
        {
            Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("BigWalkVRInstaller-Linux");
        }

        public static async Task<Manifest> FetchManifest(string url)
        {
            var json = LocalManifest != null ? File.ReadAllText(LocalManifest) : await FetchText(url);
            var manifest = JsonUtil.Deserialize<Manifest>(json);
            if (manifest?.schemaVersion != 3 || manifest.bepinex == null || manifest.mods == null) throw new Exception("invalid manifest schema");
            return manifest;
        }

        public static async Task<string> FetchText(string url)
        {
            RequireHttps(url);
            // cache buster, raw.githubusercontent holds manifests for a few minutes
            var bust = url + (url.Contains("?") ? "&" : "?") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return await Http.GetStringAsync(bust);
        }

        // the whole cause chain, a bare "An error occurred while sending the request" helps nobody
        public static string DescribeError(Exception error)
        {
            var details = new StringBuilder();
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                if (details.Length > 0) details.AppendLine();
                details.Append($"{cause.GetType().Name} (0x{cause.HResult:X8}): {cause.Message}");
                if (cause is HttpRequestException http)
                    details.Append($" [network: {http.HttpRequestError}{(http.StatusCode != null ? $", HTTP {(int)http.StatusCode}" : "")}]");
            }
            return details.ToString();
        }

        public static async Task<byte[]> Download(string url, string expectedSha256, IProgress<double> progress = null)
        {
            ValidateSha256(expectedSha256);

            byte[] bytes;
            if (LocalManifest != null && Uri.TryCreate(url, UriKind.Absolute, out var local) && local.IsFile)
            {
                using (var stream = File.OpenRead(local.LocalPath))
                    bytes = await Read(stream, stream.Length, progress);
            }
            else
            {
                RequireHttps(url);
                using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync())
                        bytes = await Read(stream, response.Content.Headers.ContentLength ?? 0, progress);
                }
            }

            var actual = Sha256(bytes);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"sha256 mismatch for {url}");
            return bytes;
        }

        static async Task<byte[]> Read(Stream stream, long total, IProgress<double> progress)
        {
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (total > 0) progress?.Report((double)buffer.Length / total);
                }
                return buffer.ToArray();
            }
        }

        static void RequireHttps(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"URL must use HTTPS: {url}");
        }

        static void ValidateSha256(string value)
        {
            if (value == null || value.Length != 64) throw new Exception("download requires a SHA-256 hash");
            foreach (var c in value)
                if (!Uri.IsHexDigit(c)) throw new Exception("download requires a valid SHA-256 hash");
        }

        public static string Sha256(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
