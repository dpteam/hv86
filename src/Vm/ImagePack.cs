using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Что делать со скачанным файлом, если это не готовый образ.
    ///
    /// Каталоги старых ОС (old-dos.ru и подобные) отдают образы как придётся:
    /// то `iso`, то `img`, то ZIP с образом внутри, то ZIP с россыпью файлов
    /// дискеты, то RAR/7z. Первые два мы умеем и так, а остальное надо
    /// распознать до того, как файл назовут «диском» и ВМ не загрузится.
    ///
    /// Правила:
    ///   * `PK..`  - ZIP: если внутри ровно один образ - распаковываем его;
    ///   * `Rar!` / `7z..` - честно отказываем: в BCL их нет, а тащить
    ///     сторонний распаковщик ради каталога - не то, за что стоит платить;
    ///   * `CD001` по смещению 0x8001 - ISO (CD);
    ///   * всё остальное - считаем образом как есть (флоппи/винчестер).
    /// </summary>
    public static class ImagePack
    {
        /// <summary>Расширения, которые считаем образом.</summary>
        private static readonly string[] ImageExtensions =
        {
            ".img", ".iso", ".ima", ".flp", ".vfd", ".dsk", ".raw", ".bin", ".hdd"
        };

        /// <summary>Стандартные размеры носителей: по ним узнаём образ в архиве.</summary>
        private static readonly long[] KnownSizes =
        {
            512,                                     // boot-сектор
            163840, 184320, 327680,                  // 160K/180K/320K
            368640,                                  // 360K
            737280,                                  // 720K
            1228800, 1474560, 2949120                // 1.2M / 1.44M / 2.88M
        };

        public const string KindZip = "zip";
        public const string KindRar = "rar";
        public const string KindSevenZip = "7z";
        public const string KindIso = "iso";
        public const string KindRaw = "raw";

        /// <summary>Определяет формат по сигнатуре (расширению не верим).</summary>
        public static string Detect(string file)
        {
            try
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] head = new byte[8];

                    if (stream.Read(head, 0, head.Length) < 4) return KindRaw;

                    if (head[0] == 'P' && head[1] == 'K' && (head[2] == 3 || head[2] == 5)) return KindZip;
                    if (head[0] == 'R' && head[1] == 'a' && head[2] == 'r') return KindRar;
                    if (head[0] == '7' && head[1] == 'z') return KindSevenZip;

                    // ISO: primary volume descriptor "CD001" на смещении 0x8001
                    if (stream.Length > 0x8006)
                    {
                        stream.Seek(0x8001, SeekOrigin.Begin);

                        byte[] id = new byte[5];
                        if (stream.Read(id, 0, 5) == 5 &&
                            id[0] == 'C' && id[1] == 'D' && id[2] == '0' && id[3] == '0' && id[4] == '1')
                            return KindIso;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("PRESET", "сигнатура " + Path.GetFileName(file) + ": " + ex.Message);
            }

            return KindRaw;
        }

        /// <summary>
        /// Возвращает путь к настоящему образу. ZIP с единственным образом
        /// распаковывается рядом с архивом; RAR/7z и «россыпь файлов» - отказ
        /// с понятным текстом, чтобы не создавать ВМ, которая не загрузится.
        /// </summary>
        public static string Unwrap(string file)
        {
            string kind = Detect(file);

            if (kind == KindRar || kind == KindSevenZip)
                throw new Hv86Exception("образ упакован в " + kind.ToUpperInvariant() +
                                        ": этот формат не поддерживается, распакуйте вручную и подключите файлом");

            if (kind != KindZip) return file;

            return UnzipImage(file);
        }

        private static string UnzipImage(string archive)
        {
            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(archive))
                {
                    List<ZipArchiveEntry> images = zip.Entries
                        .Where(e => e.Length > 0 && e.FullName.IndexOf('/') < 0 && e.FullName.IndexOf('\\') < 0)
                        .Where(e => Array.IndexOf(ImageExtensions, Path.GetExtension(e.Name).ToLowerInvariant()) >= 0)
                        .ToList();

                    ZipArchiveEntry pick = Single(images);

                    // в архиве может быть и один образ, и куча файлов дискеты:
                    // отличаем по размеру - образ совпадает со стандартным
                    if (pick == null) pick = Single(images.Where(e => IsKnownSize(e.Length)).ToList());

                    if (pick == null)
                        throw new Hv86Exception("в архиве нет одного образа (" + images.Count +
                                                " кандидатов): " + string.Join(", ",
                                                    images.Take(5).Select(e => e.Name)));

                    string target = Path.Combine(Path.GetDirectoryName(archive) ?? ".",
                        Path.GetFileNameWithoutExtension(archive) + "-" + pick.Name);

                    // уже распаковано в прошлый раз
                    if (File.Exists(target)) return target;

                    using (Stream source = pick.Open())
                    using (FileStream destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        source.CopyTo(destination);
                    }

                    Log.Info("PRESET", "распакован образ из архива: " + pick.Name);
                    return target;
                }
            }
            catch (Hv86Exception)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new Hv86Exception("не удалось разобрать архив " + Path.GetFileName(archive) + ": " + ex.Message);
            }
        }

        /// <summary>Единственный элемент списка или null, если их 0 или больше одного.</summary>
        private static ZipArchiveEntry Single(List<ZipArchiveEntry> items)
        {
            return items.Count == 1 ? items[0] : null;
        }

        private static bool IsKnownSize(long length)
        {
            foreach (long size in KnownSizes)
                if (length == size) return true;

            return false;
        }
    }
}