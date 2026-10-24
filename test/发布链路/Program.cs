using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.Updater;

internal static class Program
{
    private static int _failed;
    private static int _passed;
    private static int Main()
    {
        Run("更新清单接受同仓库正式包", () =>
        {
            var manifest = ReleaseManifest.Parse(Manifest(), "ChenBit/LiteMonitor");
            Check(manifest.Version == "1.3.9", "版本解析错误");
        });
        Run("更新清单拒绝外部地址、旧命名、预发行版本及缺少校验和", () =>
        {
            foreach (string json in new[] {
                Manifest().Replace("ChenBit/LiteMonitor/releases", "Diorser/LiteMonitor/releases"),
                Manifest().Replace("LiteMonitor-v", "LiteMonitor_v"),
                Manifest().Replace("1.3.9", "1.3.9-rc.1"),
                Manifest().Replace(new string('a', 64), "invalid")
            }) ExpectFailure(() => ReleaseManifest.Parse(json, "ChenBit/LiteMonitor"));
        });
        Run("未修改内置插件更新，自定义主题、语言、配置及插件保留", () => WithPackages((source, target) =>
        {
            Seed(target, "old");
            Seal(target);
            Write(target, "resources/themes/Custom.json", "user theme");
            Write(target, "resources/lang/zh.json", "user translation");
            Write(target, "resources/plugins/User.json", "user plugin");
            Write(target, "settings.json", "user settings");
            Seed(source, "new");
            Seal(source);
            UpdatePackage.Apply(source, target);
            Check(Read(target, "LiteMonitor.exe") == "new", "主程序没有更新");
            Check(Read(target, "resources/plugins/Builtin.json") == "new", "内置插件没有更新");
            Check(Read(target, "resources/themes/Custom.json") == "user theme", "自定义主题被覆盖");
            Check(Read(target, "resources/lang/zh.json") == "user translation", "自定义语言被覆盖");
            Check(Read(target, "resources/plugins/User.json") == "user plugin", "用户插件丢失");
            Check(Read(target, "settings.json") == "user settings", "用户配置被覆盖");
            Check(Read(target, "resources/LiteMonitor.Updater.exe") == "old", "正在运行的更新器被覆盖");
        }));
        Run("旧安装没有清单时保留已有插件，补充新增资源", () => WithPackages((source, target) =>
        {
            Seed(target, "legacy");
            Seed(source, "new");
            Write(source, "resources/plugins/Added.json", "added");
            Seal(source);
            UpdatePackage.Apply(source, target);
            Check(Read(target, "resources/plugins/Builtin.json") == "legacy", "旧安装插件被覆盖");
            Check(Read(target, "resources/plugins/Added.json") == "added", "新增资源未复制");
        }));
        Run("被修改的内置插件连续更新仍然保留", () => WithPackages((source, target) =>
        {
            Seed(target, "old"); Seal(target);
            Write(target, "resources/plugins/Builtin.json", "my credentials and edits");
            Seed(source, "new"); Seal(source);
            UpdatePackage.Apply(source, target);
            Seed(source, "newer"); Seal(source);
            UpdatePackage.Apply(source, target);
            Check(Read(target, "resources/plugins/Builtin.json") == "my credentials and edits", "用户改动被覆盖");
        }));
        Run("损坏或缺少文件时在覆盖前失败", () => WithPackages((source, target) =>
        {
            Seed(target, "old"); Seal(target);
            string previous = Read(target, "release-files.json");
            Seed(source, "new"); Seal(source);
            Write(source, "resources/plugins/Builtin.json", "corrupt");
            ExpectFailure(() => UpdatePackage.Apply(source, target));
            Check(Read(target, "LiteMonitor.exe") == "old", "损坏包覆盖了主程序");
            Check(Read(target, "release-files.json") == previous, "失败却提交了新版清单");
        }));
        Run("拒绝更新清单路径越界", () => WithPackages((source, target) =>
        {
            Seed(source, "new"); Seal(source);
            string json = Read(source, "release-files.json").Replace("resources/plugins/Builtin.json", "../../outside.json");
            Write(source, "release-files.json", json);
            ExpectFailure(() => UpdatePackage.Apply(source, target));
            Check(!File.Exists(Path.Combine(target, "LiteMonitor.exe")), "越界包开始了覆盖");
        }));
        Console.WriteLine($"发布链路：通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    private static string Manifest() => JsonSerializer.Serialize(new {
        version = "1.3.9", changelog = "修复更新", releaseDate = "2026-10-24",
        downloadUrl = "https://github.com/ChenBit/LiteMonitor/releases/download/v1.3.9/LiteMonitor-v1.3.9-win-x64.zip",
        sha256 = new string('a', 64)
    });
    private static void Seed(string directory, string content)
    {
        foreach (string path in new[] { "LiteMonitor.exe", "resources/LiteMonitor.Updater.exe", "resources/plugins/Builtin.json", "resources/themes/Custom.json", "resources/lang/zh.json" }) Write(directory, path, content);
    }
    private static void Seal(string directory)
    {
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "release-files.json")
            .ToDictionary(f => Path.GetRelativePath(directory, f).Replace('\\', '/'), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant());
        Write(directory, "release-files.json", JsonSerializer.Serialize(new { version = "1.3.9", files }));
    }
    private static string Read(string directory, string path) => File.ReadAllText(Path.Combine(directory, path));
    private static void Write(string directory, string path, string content)
    {
        string full = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }
    private static void WithPackages(Action<string, string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "LiteMonitor-tests-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try { test(source, target); } finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; } catch (FileNotFoundException) { return; }
        throw new Exception("本应拒绝无效输入");
    }
    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("通过：" + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("失败：" + name + "：" + ex.Message); }
    }
}
