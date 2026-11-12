using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace LolModoCine
{
    /// <summary>Lo que se configura desde la ventana. Vive en %APPDATA%\LolModoCine\config.ini.</summary>
    sealed class Config
    {
        public const string LolGame = "league of legends.exe";
        public const string TftLauncher = "tftclient.exe";
        // TFT es un cliente aparte hecho en Unreal: la ventana del juego la crea este proceso.
        public const string TftGame = "tftclient-win64-shipping.exe";

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LolModoCine");

        static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

        // Que se oculta mientras el juego tiene el foco.
        public bool HideTaskbar = true;
        public bool HideIcons = true;
        public bool BlackBackground = true;

        // Que juegos activan el modo cine.
        public bool GameLol = true;
        public bool GameTft = true;
        // Otros exe, separados por coma. Sin lugar en la ventana por ahora: solo editando el ini.
        public string OtherGames = "";

        // Monitor mas chico. Width/Height en 0 = todavia no se calculo.
        public double MonitorInches = 27;
        public double TargetInches = 24.5;
        public int Width;
        public int Height;
        public string LolPath = "";
        public int LolMode;  // 0 no tocar, 1 ventana, 2 sin bordes
        public bool PinTft;

        public bool Autostart = true;
        public bool Paused;
        public bool TrayHintShown;

        public static Config Load()
        {
            var c = new Config();
            string[] lines;
            try
            {
                lines = File.ReadAllLines(FilePath);
            }
            catch (Exception)  // primera vez, o archivo ilegible: valores por defecto
            {
                return c;
            }

            foreach (string raw in lines)
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string key = raw.Substring(0, eq).Trim();
                string value = raw.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "HideTaskbar": c.HideTaskbar = value == "1"; break;
                    case "HideIcons": c.HideIcons = value == "1"; break;
                    case "BlackBackground": c.BlackBackground = value == "1"; break;
                    case "GameLol": c.GameLol = value == "1"; break;
                    case "GameTft": c.GameTft = value == "1"; break;
                    case "OtherGames": c.OtherGames = value; break;
                    case "MonitorInches": c.MonitorInches = ParseDouble(value, c.MonitorInches); break;
                    case "TargetInches": c.TargetInches = ParseDouble(value, c.TargetInches); break;
                    case "Width": c.Width = ParseInt(value); break;
                    case "Height": c.Height = ParseInt(value); break;
                    case "LolPath": c.LolPath = value; break;
                    case "LolMode": c.LolMode = Math.Max(0, Math.Min(2, ParseInt(value))); break;
                    case "PinTft": c.PinTft = value == "1"; break;
                    case "Autostart": c.Autostart = value == "1"; break;
                    case "Paused": c.Paused = value == "1"; break;
                    case "TrayHintShown": c.TrayHintShown = value == "1"; break;
                }
            }
            return c;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            Action<string, object> put = (k, v) => sb.Append(k).Append('=').Append(v).Append("\r\n");
            Func<bool, string> bit = b => b ? "1" : "0";

            put("HideTaskbar", bit(HideTaskbar));
            put("HideIcons", bit(HideIcons));
            put("BlackBackground", bit(BlackBackground));
            put("GameLol", bit(GameLol));
            put("GameTft", bit(GameTft));
            put("OtherGames", OtherGames);
            put("MonitorInches", MonitorInches.ToString(CultureInfo.InvariantCulture));
            put("TargetInches", TargetInches.ToString(CultureInfo.InvariantCulture));
            put("Width", Width);
            put("Height", Height);
            put("LolPath", LolPath);
            put("LolMode", LolMode);
            put("PinTft", bit(PinTft));
            put("Autostart", bit(Autostart));
            put("Paused", bit(Paused));
            put("TrayHintShown", bit(TrayHintShown));

            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Sin poder guardar la app sigue andando con lo que tiene en memoria.
            }
        }

        /// <summary>Nombres de exe (en minusculas) que activan el modo cine.</summary>
        public HashSet<string> Games()
        {
            var set = new HashSet<string>();
            if (GameLol) set.Add(LolGame);
            if (GameTft)
            {
                set.Add(TftLauncher);
                set.Add(TftGame);
            }
            foreach (string part in OtherGames.Split(',', ';'))
            {
                string name = part.Trim().Trim('"').ToLowerInvariant();
                if (name.Length == 0) continue;
                if (!name.EndsWith(".exe")) name += ".exe";
                set.Add(name);
            }
            return set;
        }

        static double ParseDouble(string text, double fallback)
        {
            double v;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0
                ? v : fallback;
        }

        static int ParseInt(string text)
        {
            int v;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }

    /// <summary>Arranque con Windows: una entrada en HKCU\...\Run, sin permisos de administrador.</summary>
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "LolModoCine";
        public const string TrayArg = "--tray";

        /// <summary>Tambien corrige la ruta si el exe se movio de carpeta.</summary>
        public static void Set(bool enabled, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled)
                    {
                        string wanted = "\"" + exePath + "\" " + TrayArg;
                        if (!wanted.Equals(key.GetValue(ValueName) as string, StringComparison.OrdinalIgnoreCase))
                            key.SetValue(ValueName, wanted);
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception)
            {
                // Registro bloqueado por politica: el resto de la app no depende de esto.
            }
        }
    }
}
