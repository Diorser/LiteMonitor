using System.Text.Json;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    internal static class ReleaseSmokeTest
    {
        public static void Run()
        {
            try
            {
                foreach (string folder in new[] { "themes", "lang", "plugins" })
                {
                    string[] files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "resources", folder), "*.json");
                    if (files.Length == 0) throw new InvalidDataException($"缺少 {folder} 资源");
                    foreach (string file in files)
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    }
                }
                ApplicationConfiguration.Initialize();
                ThemeManager.Load("DarkFlat_Classic");
                LanguageManager.Load("en");
                _ = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName();
                using var form = new Form { Text = "LiteMonitor smoke test", ShowInTaskbar = false, Opacity = 0 };
                form.Shown += (_, _) => form.BeginInvoke(new Action(form.Close));
                Application.Run(form);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-result.json"), JsonSerializer.Serialize(new { version = UpdateChecker.GetCurrentVersion() }));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-test-result.json"), JsonSerializer.Serialize(new { error = ex.ToString() }));
                Environment.ExitCode = 1;
            }
        }
    }
}
