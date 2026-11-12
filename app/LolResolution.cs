using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LolModoCine
{
    /// <summary>
    /// Convierte un monitor grande en uno mas chico para League of Legends: escribe una
    /// resolucion proporcionalmente menor en su game.cfg. Mismo criterio que set-resolution.py.
    /// </summary>
    static class LolResolution
    {
        const string DefaultInstall = @"C:\Riot Games\League of Legends";
        const string Section = "General";

        // Deducido de configs reales: 0 pantalla completa, 1 sin bordes, 2 ventana.
        const int ModeFullscreen = 0;
        const int ModeBorderless = 1;
        const int ModeWindowed = 2;

        // El cliente y el juego reescriben game.cfg al cerrarse y desharian el cambio.
        static readonly string[] Processes = { "League of Legends", "LeagueClient", "LeagueClientUx" };

        public sealed class Result
        {
            public readonly List<string> Changes = new List<string>();
            public string ConfigPath;
            public bool Fullscreen;  // quedo en pantalla completa: la resolucion chica se estira
        }

        /// <summary>Misma relacion de aspecto, escalada por la diagonal. Redondeada a pares.</summary>
        public static Size Scaled(Size native, double monitorInches, double targetInches)
        {
            double factor = Math.Min(1.0, targetInches / monitorInches);
            int w = (int)Math.Round(native.Width * factor);
            int h = (int)Math.Round(native.Height * factor);
            return new Size(w - (w % 2), h - (h % 2));
        }

        /// <summary>Carpeta de instalacion segun el Riot Client, o la ruta por defecto.</summary>
        public static string FindInstall()
        {
            try
            {
                string meta = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml");
                Match m = Regex.Match(File.ReadAllText(meta),
                    "^product_install_full_path:\\s*\"?([^\"\\r\\n]+)\"?", RegexOptions.Multiline);
                if (m.Success)
                {
                    string path = m.Groups[1].Value.Trim().Replace('/', '\\');
                    if (Directory.Exists(path)) return path;
                }
            }
            catch (Exception)
            {
                // Sin Riot Client instalado, o en otra ruta: se usa la de siempre.
            }
            return DefaultInstall;
        }

        /// <summary>Acepta la carpeta de instalacion, la carpeta Config o el game.cfg mismo.</summary>
        public static string FindConfig(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (path.Length == 0) return null;
            if (File.Exists(path)) return path;
            foreach (string candidate in new[]
            {
                Path.Combine(path, "Config", "game.cfg"),
                Path.Combine(path, "game.cfg"),
            })
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public static List<string> RunningProcesses()
        {
            var found = new List<string>();
            foreach (string name in Processes)
            {
                Process[] list = Process.GetProcessesByName(name);
                if (list.Length > 0) found.Add(name + ".exe");
                foreach (Process p in list) p.Dispose();
            }
            return found;
        }

        /// <param name="mode">0 no tocar, 1 ventana, 2 sin bordes.</param>
        public static Result Apply(string configPath, int width, int height, int mode)
        {
            byte[] raw = File.ReadAllBytes(configPath);

            // El BOM se maneja aparte para no agregar uno donde no lo habia: game.cfg sin BOM
            // + BOM = archivo que el juego puede no parsear.
            bool hadBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            int offset = hadBom ? 3 : 0;
            Encoding encoding = new UTF8Encoding(false, true);
            string text;
            try
            {
                text = encoding.GetString(raw, offset, raw.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.GetEncoding(1252);
                text = encoding.GetString(raw, offset, raw.Length - offset);
            }

            var result = new Result { ConfigPath = configPath };
            var wanted = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Width", width.ToString()),
                new KeyValuePair<string, string>("Height", height.ToString()),
            };
            if (mode == 1) wanted.Add(new KeyValuePair<string, string>("WindowMode", ModeWindowed.ToString()));
            if (mode == 2) wanted.Add(new KeyValuePair<string, string>("WindowMode", ModeBorderless.ToString()));

            foreach (KeyValuePair<string, string> kv in wanted)
            {
                string old;
                text = SetKey(text, Section, kv.Key, kv.Value, out old);
                if (old != kv.Value)
                    result.Changes.Add(kv.Key + ": " + (old ?? "(nuevo)") + " → " + kv.Value);
            }

            string current;
            SetKey(text, Section, "WindowMode", "", out current);
            result.Fullscreen = current == ModeFullscreen.ToString();

            if (result.Changes.Count == 0) return result;

            string backup = configPath + ".bak";
            if (!File.Exists(backup)) File.Copy(configPath, backup);  // el original, no la ultima corrida

            // Mucha gente marca game.cfg como solo-lectura para que el juego no lo pise al
            // salir. Se destilda para escribir y se vuelve a dejar como estaba.
            FileAttributes attributes = File.GetAttributes(configPath);
            bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
            if (readOnly) File.SetAttributes(configPath, attributes & ~FileAttributes.ReadOnly);
            try
            {
                using (var stream = new FileStream(configPath, FileMode.Create, FileAccess.Write))
                {
                    if (hadBom) stream.Write(raw, 0, 3);
                    byte[] body = encoding.GetBytes(text);
                    stream.Write(body, 0, body.Length);
                }
            }
            finally
            {
                if (readOnly) File.SetAttributes(configPath, attributes);
            }
            return result;
        }

        /// <summary>
        /// Cambia (o agrega) key=value dentro de section sin tocar nada mas: comentarios, orden
        /// y saltos de linea quedan como estaban. Deja en old el valor anterior, o null.
        /// </summary>
        static string SetKey(string text, string section, string key, string value, out string old)
        {
            List<string> lines = SplitKeepingEol(text);
            string target = section.Trim().ToLowerInvariant();
            string keyLower = key.ToLowerInvariant();
            string current = null;
            int lastLine = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string stripped = line.Trim();
                if (stripped.StartsWith("[") && stripped.EndsWith("]"))
                {
                    if (current == target) break;
                    current = stripped.Substring(1, stripped.Length - 2).Trim().ToLowerInvariant();
                    if (current == target) lastLine = i;  // cabecera de la seccion buscada
                    continue;
                }
                if (current != target || stripped.Length == 0 || stripped.StartsWith(";")) continue;

                lastLine = i;
                int eq = stripped.IndexOf('=');
                string name = (eq >= 0 ? stripped.Substring(0, eq) : stripped).Trim().ToLowerInvariant();
                if (name != keyLower) continue;

                old = eq >= 0 ? stripped.Substring(eq + 1).Trim() : "";
                if (old == value) return text;
                string eol = line.Substring(line.TrimEnd('\r', '\n').Length);
                lines[i] = key + "=" + value + (eol.Length > 0 ? eol : "\n");
                return string.Concat(lines);
            }

            old = null;
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string entry = key + "=" + value + newline;
            if (lastLine < 0)  // la seccion no existe
            {
                string prefix = (text.Length == 0 || text.EndsWith("\n")) ? "" : newline;
                return text + prefix + "[" + section + "]" + newline + entry;
            }
            if (!lines[lastLine].EndsWith("\n")) lines[lastLine] += newline;  // ultima linea sin salto
            lines.Insert(lastLine + 1, entry);
            return string.Concat(lines);
        }

        static List<string> SplitKeepingEol(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                lines.Add(text.Substring(start, i - start + 1));
                start = i + 1;
            }
            if (start < text.Length) lines.Add(text.Substring(start));
            return lines;
        }
    }
}
