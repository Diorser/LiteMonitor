using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiteMonitor
{
    internal sealed class ReleaseManifest
    {
        public string Version { get; private init; } = "";
        public string Changelog { get; private init; } = "";
        public string ReleaseDate { get; private init; } = "";
        public string DownloadUrl { get; private init; } = "";
        public string Sha256 { get; private init; } = "";

        public static string Repository => Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ReleaseRepository")?.Value ?? "ChenBit/LiteMonitor";

        public static ReleaseManifest Parse(string json, string repository)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string version = root.GetProperty("version").GetString() ?? "";
            string url = root.GetProperty("downloadUrl").GetString() ?? "";
            string sha = root.GetProperty("sha256").GetString() ?? "";
            string expected = $"https://github.com/{repository}/releases/download/v{version}/LiteMonitor-v{version}-win-x64.zip";
            if (!Regex.IsMatch(repository, @"^[\w.-]+/[\w.-]+$") ||
                !Regex.IsMatch(version, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$") ||
                !System.Version.TryParse(version, out _) || url != expected || !Regex.IsMatch(sha, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("更新清单的版本、下载地址或校验和无效。");
            return new ReleaseManifest
            {
                Version = version, DownloadUrl = url, Sha256 = sha,
                Changelog = root.GetProperty("changelog").GetString() ?? "",
                ReleaseDate = root.GetProperty("releaseDate").GetString() ?? ""
            };
        }
    }
}
