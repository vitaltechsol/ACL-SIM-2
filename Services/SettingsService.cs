using System;
using System.IO;
using System.Text.Json;
using ACL_SIM_2.Models;

namespace ACL_SIM_2.Services
{
    public static class SettingsService
    {
        private static string GetFolder()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ACL-SIM-2");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        public static void SaveAxisSettings(string axisName, AxisSettings settings)
        {
            var path = Path.Combine(GetFolder(), $"axis-{axisName}-settings.json");
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, opts));
        }

        public static AxisSettings? LoadAxisSettings(string axisName)
        {
            var path = Path.Combine(GetFolder(), $"axis-{axisName}-settings.json");
            if (!File.Exists(path)) return null;
            try
            {
                var txt = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AxisSettings>(txt);
            }
            catch
            {
                return null;
            }
        }

        public static void SaveGlobalSettings(GlobalSettings settings)
        {
            var path = Path.Combine(GetFolder(), "global-settings.json");
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(settings, opts));
        }

        public static GlobalSettings? LoadGlobalSettings()
        {
            var path = Path.Combine(GetFolder(), "global-settings.json");
            if (!File.Exists(path)) return null;
            try
            {
                var txt = File.ReadAllText(path);
                return JsonSerializer.Deserialize<GlobalSettings>(txt);
            }
            catch
            {
                return null;
            }
        }
    }
}
