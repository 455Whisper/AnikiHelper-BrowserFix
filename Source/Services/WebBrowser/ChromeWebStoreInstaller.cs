using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AnikiHelper.Services.WebBrowser
{
    /// <summary>
    /// Installs Chrome Web Store extensions for the Aniki browser: downloads the extension
    /// package (CRX) from Google's update service, the same source Chrome uses, and unpacks it
    /// into the extensions folder so WebView2 can load it as an unpacked extension.
    /// </summary>
    internal static class ChromeWebStoreInstaller
    {
        private static readonly Regex StoreUrl = new Regex(
            @"^https://(chromewebstore\.google\.com/detail/(?:[^/?#]+/)?|chrome\.google\.com/webstore/detail/(?:[^/?#]+/)?)(?<id>[a-p]{32})(?:[/?#]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HttpClient Http = CreateClient();

        /// <summary>Extension id if the address is a Chrome Web Store extension page, else null.</summary>
        public static string GetExtensionId(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return null;
            }

            var match = StoreUrl.Match(address);
            return match.Success ? match.Groups["id"].Value.ToLowerInvariant() : null;
        }

        public static string FolderFor(string extensionsFolder, string id)
        {
            return Path.Combine(extensionsFolder, "webstore_" + id);
        }

        /// <summary>Downloads and unpacks the extension; returns the folder it was unpacked to.</summary>
        public static async Task<string> DownloadAndUnpackAsync(string id, string extensionsFolder, string browserVersion)
        {
            var version = ChromeVersion(browserVersion);
            var url = "https://clients2.google.com/service/update2/crx?response=redirect&os=win&arch=x64&os_arch=x86_64" +
                      "&nacl_arch=x86-64&prod=chromecrx&prodchannel=&prodversion=" + version +
                      "&lang=en-US&acceptformat=crx2,crx3&x=id%3D" + id + "%26installsource%3Dondemand%26uc";

            byte[] crx;
            using (var response = await Http.GetAsync(url).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("The Chrome Web Store returned HTTP " + (int)response.StatusCode + ".");
                }

                crx = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }

            var zipStart = FindZipStart(crx);
            var target = FolderFor(extensionsFolder, id);
            var staging = target + ".installing";
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }

            Directory.CreateDirectory(staging);
            using (var stream = new MemoryStream(crx, zipStart, crx.Length - zipStart, false))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var root = Path.GetFullPath(staging + Path.DirectorySeparatorChar);
                foreach (var entry in zip.Entries)
                {
                    var path = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // never write outside the extension's folder
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(path);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path, true);
                }
            }

            // Chrome's own signature folder isn't needed (or allowed) for unpacked extensions.
            var metadata = Path.Combine(staging, "_metadata");
            if (Directory.Exists(metadata))
            {
                try { Directory.Delete(metadata, true); } catch { }
            }

            if (!File.Exists(Path.Combine(staging, "manifest.json")))
            {
                Directory.Delete(staging, true);
                throw new InvalidOperationException("The downloaded package has no manifest.json.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }

            Directory.Move(staging, target);
            return target;
        }

        private static int FindZipStart(byte[] crx)
        {
            if (crx.Length > 16 && crx[0] == (byte)'C' && crx[1] == (byte)'r' && crx[2] == (byte)'2' && crx[3] == (byte)'4')
            {
                var version = BitConverter.ToUInt32(crx, 4);
                if (version == 3)
                {
                    return 12 + (int)BitConverter.ToUInt32(crx, 8);
                }

                if (version == 2)
                {
                    return 16 + (int)BitConverter.ToUInt32(crx, 8) + (int)BitConverter.ToUInt32(crx, 12);
                }

                throw new InvalidOperationException("Unknown extension package version " + version + ".");
            }

            if (crx.Length > 4 && crx[0] == (byte)'P' && crx[1] == (byte)'K')
            {
                return 0; // already a plain zip
            }

            throw new InvalidOperationException("The Chrome Web Store didn't return an extension package (it may not be available for this browser).");
        }

        private static string ChromeVersion(string browserVersion)
        {
            // e.g. "131.0.2903.70" -> "131.0.0.0"; fall back to a recent version.
            var major = (browserVersion ?? string.Empty).Split('.').FirstOrDefault();
            int number;
            return int.TryParse(major, out number) && number > 80 ? number + ".0.0.0" : "131.0.0.0";
        }

        private static HttpClient CreateClient()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            }
            catch
            {
            }

            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            return client;
        }
    }
}
