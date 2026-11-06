using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Hv86.App
{
    /// <summary>Кусок строки с одинаковыми цветами.</summary>
    internal struct AnsiRun
    {
        public string Text;
        public Color Foreground;
        public Color Background;
    }

    /// <summary>
    /// Разбор ANSI-цветов, которые отдаёт v86 в headless-режиме
    /// (`screen: {ansi:true}` → `\x1b[38;2;r;g;bm` / `\x1b[48;2;r;g;bm`).
    ///
    /// Вынесено из окна консоли, чтобы это можно было проверить тестом:
    /// рисование глазами не проверишь, а вот что цвета разобраны правильно —
    /// вполне.
    /// </summary>
    internal static class AnsiText
    {
        public static readonly Color DefaultForeground = Color.FromArgb(0xC8, 0xC8, 0xC8);
        public static readonly Color DefaultBackground = Color.Black;

        public static List<AnsiRun> Parse(string line, int maxColumns)
        {
            List<AnsiRun> runs = new List<AnsiRun>();

            if (string.IsNullOrEmpty(line)) return runs;

            Color foreground = DefaultForeground;
            Color background = DefaultBackground;

            StringBuilder text = new StringBuilder();
            int column = 0;
            int i = 0;

            while (i < line.Length && (maxColumns <= 0 || column < maxColumns))
            {
                if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
                {
                    int end = line.IndexOf('m', i);
                    if (end < 0) break;

                    Flush(runs, text, foreground, background);
                    ApplySgr(line.Substring(i + 2, end - i - 2), ref foreground, ref background);

                    i = end + 1;
                    continue;
                }

                text.Append(line[i]);
                column++;
                i++;
            }

            Flush(runs, text, foreground, background);
            return runs;
        }

        /// <summary>Текст без управляющих последовательностей (для логов).</summary>
        public static string Strip(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";

            StringBuilder clean = new StringBuilder();
            int i = 0;

            while (i < line.Length)
            {
                if (line[i] == '\u001b' && i + 1 < line.Length && line[i + 1] == '[')
                {
                    int end = line.IndexOf('m', i);
                    if (end < 0) break;

                    i = end + 1;
                    continue;
                }

                clean.Append(line[i]);
                i++;
            }

            return clean.ToString().TrimEnd();
        }

        private static void Flush(List<AnsiRun> runs, StringBuilder text, Color foreground, Color background)
        {
            if (text.Length == 0) return;

            runs.Add(new AnsiRun
            {
                Text = text.ToString(),
                Foreground = foreground,
                Background = background
            });

            text.Clear();
        }

        private static void ApplySgr(string spec, ref Color foreground, ref Color background)
        {
            if (string.IsNullOrEmpty(spec) || spec == "0")
            {
                foreground = DefaultForeground;
                background = DefaultBackground;
                return;
            }

            string[] parts = spec.Split(';');

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == "0")
                {
                    foreground = DefaultForeground;
                    background = DefaultBackground;
                    continue;
                }

                if ((parts[i] == "38" || parts[i] == "48") && i + 4 < parts.Length && parts[i + 1] == "2")
                {
                    Color color = Color.FromArgb(
                        ParsePart(parts[i + 2]),
                        ParsePart(parts[i + 3]),
                        ParsePart(parts[i + 4]));

                    if (parts[i] == "38") foreground = color;
                    else background = color;

                    i += 4;
                }
            }
        }

        private static int ParsePart(string text)
        {
            int value;
            return int.TryParse(text, out value) ? Math.Max(0, Math.Min(255, value)) : 255;
        }
    }
}