using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Разбор state-файла v86 (то, что возвращает save_state()).
    ///
    /// Формат (src/state.js):
    ///   [0..16)   заголовок: int32 magic 0x86768676, версия, общая длина, длина JSON
    ///   [16..)    JSON {"buffer_infos":[{offset,length}...], "state":[...]}
    ///   далее     подряд все буферы, выровненные по 4 байта
    ///
    /// Зачем нам это читать: v86 не отдаёт API «сохрани диск». Зато дисковые
    /// «грязные» блоки попадают в state как кэш записанных блоков по 256 байт
    /// (AsyncXHRBuffer.get_state). Значит, после save_state мы можем применить
    /// эти блоки к образу диска на хосте - и получить долговременное хранение
    /// без правки самого v86.
    ///
    /// Ищем блоки структурно, а не по номеру поля: массив пар [индекс,
    /// Uint8Array(256)]. Так разбор не сломается от перестановки полей в
    /// IDEInterface.get_state.
    /// </summary>
    public static class V86State
    {
        public const int Magic = unchecked((int)0x86768676);
        public const int HeaderSize = 16;
        public const int DiskBlockSize = 256;

        public sealed class Info
        {
            public long BuffersStart;
            public long FileSize;
            public List<long> Offsets = new List<long>();
            public List<long> Lengths = new List<long>();
            public Dictionary<string, object> Root;
        }

        public static Info Read(string statePath)
        {
            Info result = new Info();

            using (FileStream stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                result.FileSize = stream.Length;

                if (stream.Length < HeaderSize) throw new Hv86Exception("state-файл слишком мал");

                byte[] header = new byte[HeaderSize];
                ReadExactly(stream, header, 0, HeaderSize);

                int magic = BitConverter.ToInt32(header, 0);
                if (magic != Magic) throw new Hv86Exception("это не state-файл v86 (magic " +
                    "0x" + ((uint)magic).ToString("x8") + ")");

                int version = BitConverter.ToInt32(header, 4);
                if (version != 6) Log.Warn("STATE", "неизвестная версия формата: " + version);

                long totalLength = BitConverter.ToInt32(header, 8);
                int infoLength = BitConverter.ToInt32(header, 12);

                if (totalLength != stream.Length)
                    Log.Warn("STATE", "длина в заголовке " + totalLength + " не совпадает с файлом " + stream.Length);

                if (infoLength <= 0 || HeaderSize + infoLength > stream.Length)
                    throw new Hv86Exception("битый JSON-блок в state-файле");

                stream.Seek(HeaderSize, SeekOrigin.Begin);

                byte[] infoBytes = new byte[infoLength];
                ReadExactly(stream, infoBytes, 0, infoLength);

                result.BuffersStart = Align4(HeaderSize + infoLength);

                Dictionary<string, object> root = Json.ParseObject(Encoding.UTF8.GetString(infoBytes));
                if (root == null) throw new Hv86Exception("не удалось разобрать JSON в state-файле");

                result.Root = root;

                List<object> infos = Json.GetList(root, "buffer_infos");
                if (infos == null) throw new Hv86Exception("в state-файле нет buffer_infos");

                foreach (object item in infos)
                {
                    Dictionary<string, object> info = item as Dictionary<string, object>;
                    result.Offsets.Add(Json.GetLong(info, "offset", 0));
                    result.Lengths.Add(Json.GetLong(info, "length", 0));
                }
            }

            return result;
        }

        /// <summary>
        /// Применяет «грязные» блоки диска из state-файла к образу диска.
        /// Возвращает число применённых блоков. Вызывать только при
        /// остановленном эмуляторе.
        /// </summary>
        public static int ApplyDiskBlocks(string statePath, string diskPath)
        {
            if (!File.Exists(statePath)) throw new Hv86Exception("state-файл не найден");
            if (!File.Exists(diskPath)) throw new Hv86Exception("образ диска не найден: " + diskPath);

            Info info = Read(statePath);
            List<object> state = Json.GetList(info.Root, "state");
            if (state == null) return 0;

            List<long[]> blocks = new List<long[]>();   // [buffer_id, block_index]
            CollectDiskBlocks(state, info, blocks);

            if (blocks.Count == 0) return 0;

            int applied = 0;

            using (FileStream disk = new FileStream(diskPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            using (FileStream stateStream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] block = new byte[DiskBlockSize];

                foreach (long[] item in blocks)
                {
                    int bufferId = (int)item[0];
                    long blockIndex = item[1];
                    long offset = blockIndex * DiskBlockSize;

                    if (offset < 0 || offset + DiskBlockSize > disk.Length) continue;

                    long source = info.BuffersStart + info.Offsets[bufferId];

                    stateStream.Seek(source, SeekOrigin.Begin);
                    ReadExactly(stateStream, block, 0, DiskBlockSize);

                    disk.Seek(offset, SeekOrigin.Begin);
                    disk.Write(block, 0, DiskBlockSize);
                    applied++;
                }

                disk.Flush(true);
            }

            return applied;
        }

        /// <summary>Сколько «грязных» блоков диска лежит в state-файле.</summary>
        public static int CountDiskBlocks(string statePath)
        {
            try
            {
                Info info = Read(statePath);
                List<object> state = Json.GetList(info.Root, "state");
                if (state == null) return 0;

                List<long[]> blocks = new List<long[]>();
                CollectDiskBlocks(state, info, blocks);
                return blocks.Count;
            }
            catch
            {
                return 0;
            }
        }

        private static void CollectDiskBlocks(object node, Info info, List<long[]> result)
        {
            List<object> list = node as List<object>;
            if (list == null) return;

            // пара [индекс блока, буфер 256 байт] - это элемент кэша AsyncXHRBuffer
            if (list.Count == 2 && list[0] is long && list[1] is Dictionary<string, object>)
            {
                Dictionary<string, object> descriptor = (Dictionary<string, object>)list[1];

                if (Json.GetString(descriptor, "__state_type__", "") == "Uint8Array")
                {
                    int bufferId = Json.GetInt(descriptor, "buffer_id", -1);

                    if (bufferId >= 0 && bufferId < info.Lengths.Count &&
                        info.Lengths[bufferId] == DiskBlockSize)
                    {
                        result.Add(new[] { (long)bufferId, (long)list[0] });
                        return;
                    }
                }
            }

            foreach (object item in list) CollectDiskBlocks(item, info, result);
        }

        private static long Align4(long value)
        {
            return (value + 3) & ~3L;
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int chunk = stream.Read(buffer, offset + read, count - read);
                if (chunk <= 0) throw new Hv86Exception("неожиданный конец state-файла");
                read += chunk;
            }
        }
    }
}