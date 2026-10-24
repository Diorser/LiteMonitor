using System.Security.Cryptography;
using System.Text.Json;

namespace LiteMonitor.Updater
{
    internal static class UpdatePackage
    {
        private static Dictionary<string, string> ReadFiles(string directory)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "release-files.json")));
            return doc.RootElement.GetProperty("files").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        }

        private static string SafePath(string directory, string relative)
        {
            string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新文件路径越界");
            return path;
        }

        private static string Digest(string file)
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static bool IsCustomizable(string relative) =>
            new[] { "resources/themes/", "resources/lang/", "resources/plugins/" }
                .Any(prefix => relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        public static void Apply(string source, string destination)
        {
            var files = ReadFiles(source);
            if (!files.ContainsKey("LiteMonitor.exe") || !files.ContainsKey("resources/LiteMonitor.Updater.exe"))
                throw new InvalidDataException("更新包缺少主程序或更新器");
            // 先完整验证，避免损坏的包进入文件覆盖阶段。
            foreach (var file in files)
            {
                string path = SafePath(source, file.Key);
                if (!File.Exists(path) || !Digest(path).Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"更新文件校验失败：{file.Key}");
            }
            Dictionary<string, string> previous;
            try { previous = ReadFiles(destination); }
            catch { previous = new(StringComparer.OrdinalIgnoreCase); }

            foreach (var file in files)
            {
                string relative = file.Key.Replace('\\', '/');
                // 用户状态不随程序发布，更新器本体已由主程序预先替换。
                if (relative.Equals("resources/LiteMonitor.Updater.exe", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("TrafficHistory.json", StringComparison.OrdinalIgnoreCase)) continue;
                string target = SafePath(destination, relative);
                if (IsCustomizable(relative) && File.Exists(target) &&
                    (!previous.TryGetValue(file.Key, out string? oldHash) || !Digest(target).Equals(oldHash, StringComparison.OrdinalIgnoreCase))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool copied = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try { File.Copy(SafePath(source, file.Key), target, true); copied = true; break; }
                    catch (IOException) when (attempt < 9) { Thread.Sleep(500); }
                    catch (UnauthorizedAccessException) when (attempt < 9) { Thread.Sleep(500); }
                }
                if (!copied) throw new IOException($"无法更新文件：{relative}");
            }
            // 全部复制成功后才记录新版清单。
            File.Copy(Path.Combine(source, "release-files.json"), Path.Combine(destination, "release-files.json"), true);
        }
    }
}
