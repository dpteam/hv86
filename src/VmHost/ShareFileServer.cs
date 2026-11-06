using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Hv86.Core;

namespace Hv86.VmHost
{
    /// <summary>
    /// Файловые операции общей папки для web-движка.
    ///
    /// Зачем отдельно от 9p: протокол 9p живёт в JS (`engine/fs9p.mjs`), а
    /// файлы — здесь. Страница присылает относительный путь внутри шары и
    /// операцию; настоящий путь и проверки «не выйти наружу» делает только
    /// этот класс, потому что странице доверять нельзя.
    ///
    /// Интерфейс совпадает с `engine/fs-node.mjs` (headless), чтобы протокол
    /// был один и тот же.
    /// </summary>
    public sealed class ShareFileServer
    {
        private readonly string _root;
        private readonly string _rootReal;
        private readonly bool _readonly;

        public ShareFileServer(string root, bool readOnly)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                throw new Hv86Exception("общая папка не найдена: " + root);

            _root = Path.GetFullPath(root);
            _readonly = readOnly;

            try
            {
                FileSystemInfo info = new DirectoryInfo(_root);
                FileSystemInfo target = info.ResolveLinkTarget(true);
                _rootReal = target != null ? target.FullName : _root;
            }
            catch
            {
                _rootReal = _root;
            }
        }

        public string Root { get { return _root; } }
        public bool ReadOnly { get { return _readonly; } }

        // ------------------------------------------------------------- запрос

        /// <summary>Выполняет операцию. Ошибки возвращаются как {error:"ENOENT"}.</summary>
        public Dictionary<string, object> Execute(Dictionary<string, object> request)
        {
            try
            {
                return Run(request);
            }
            catch (Hv86Exception ex)
            {
                return Json.Obj("error", ex.Message);
            }
            catch (Exception ex)
            {
                return Json.Obj("error", CodeOf(ex));
            }
        }

        private Dictionary<string, object> Run(Dictionary<string, object> request)
        {
            string op = Json.GetString(request, "op", "");

            switch (op)
            {
                case "ping":
                    return Json.Obj("ok", true);

                case "stat":
                {
                    string full = Host(Json.GetString(request, "path", ""));
                    Verify(full);

                    return StatOf(full);
                }

                case "readdir":
                {
                    string full = Host(Json.GetString(request, "path", ""));
                    Verify(full);

                    List<object> entries = new List<object>();

                    foreach (FileSystemInfo info in new DirectoryInfo(full).EnumerateFileSystemInfos())
                        entries.Add(Json.Obj("name", info.Name, "dir", info is DirectoryInfo));

                    return Json.Obj("entries", entries);
                }

                case "read":
                {
                    string full = Host(Json.GetString(request, "path", ""));
                    Verify(full);

                    long offset = Json.GetLong(request, "offset", 0);
                    int count = Math.Max(1, Math.Min(Json.GetInt(request, "count", 4096), 1024 * 1024));

                    byte[] buffer = new byte[count];
                    int read;

                    using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite))
                    {
                        if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
                        read = stream.Read(buffer, 0, count);
                    }

                    byte[] data = new byte[read];
                    Buffer.BlockCopy(buffer, 0, data, 0, read);

                    return Json.Obj("data", Convert.ToBase64String(data));
                }

                case "write":
                    RequireWritable();

                    {
                        string full = Host(Json.GetString(request, "path", ""));
                        long offset = Json.GetLong(request, "offset", 0);
                        byte[] data = Convert.FromBase64String(Json.GetString(request, "data", ""));

                        bool exists = File.Exists(full);

                        if (exists) Verify(full);
                        else VerifyParent(full);

                        using (FileStream stream = new FileStream(full, exists ? FileMode.Open : FileMode.CreateNew,
                                   FileAccess.Write, FileShare.ReadWrite))
                        {
                            if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
                            stream.Write(data, 0, data.Length);
                            stream.Flush(true);
                        }

                        return Json.Obj("written", data.Length);
                    }

                case "create":
                    RequireWritable();

                    {
                        string full = Host(Json.GetString(request, "path", ""));
                        VerifyParent(full);

                        if (File.Exists(full) || Directory.Exists(full))
                            throw new Hv86Exception("EEXIST");

                        using (new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        {
                        }

                        return Json.Obj("ok", true);
                    }

                case "truncate":
                    RequireWritable();

                    {
                        string full = Host(Json.GetString(request, "path", ""));
                        Verify(full);

                        using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Write,
                                   FileShare.ReadWrite))
                        {
                            stream.SetLength(Json.GetLong(request, "size", 0));
                        }

                        return Json.Obj("ok", true);
                    }

                case "mkdir":
                    RequireWritable();

                    {
                        string full = Host(Json.GetString(request, "path", ""));

                        if (Directory.Exists(full) || File.Exists(full)) throw new Hv86Exception("EEXIST");

                        VerifyParent(full);
                        Directory.CreateDirectory(full);

                        return Json.Obj("ok", true);
                    }

                case "remove":
                {
                    RequireWritable();

                    string full = Host(Json.GetString(request, "path", ""));
                    Verify(full);

                    if (Json.GetBool(request, "dir", false))
                    {
                        if (!Directory.Exists(full)) throw new Hv86Exception("ENOTDIR");
                        Directory.Delete(full, false);
                    }
                    else
                    {
                        if (Directory.Exists(full)) throw new Hv86Exception("EISDIR");
                        File.Delete(full);
                    }

                    return Json.Obj("ok", true);
                }

                case "rename":
                {
                    RequireWritable();

                    string from = Host(Json.GetString(request, "from", ""));
                    string to = Host(Json.GetString(request, "to", ""));

                    Verify(from);
                    VerifyParent(to);

                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to, true);

                    return Json.Obj("ok", true);
                }

                case "readlink":
                {
                    string full = Host(Json.GetString(request, "path", ""));

                    string target = null;

                    try
                    {
                        FileSystemInfo info = Directory.Exists(full)
                            ? (FileSystemInfo)new DirectoryInfo(full)
                            : new FileInfo(full);

                        target = info.LinkTarget;
                    }
                    catch
                    {
                        target = null;
                    }

                    return Json.Obj("target", target);
                }

                case "chmod":
                case "fsync":
                {
                    // на Windows это не имеет смысла: права - ACL, fsync делает
                    // каждая запись (Flush(true))
                    string full = Host(Json.GetString(request, "path", ""));

                    if (File.Exists(full) || Directory.Exists(full)) Verify(full);

                    return Json.Obj("ok", true);
                }

                case "statfs":
                    return Statfs();
            }

            throw new Hv86Exception("EOPNOTSUPP");
        }

        // ------------------------------------------------------------ утилиты

        private void RequireWritable()
        {
            if (_readonly) throw new Hv86Exception("EROFS");
        }

        /// <summary>Относительный путь внутри шары -> настоящий путь.</summary>
        private string Host(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return _root;

            string[] parts = relative.Split('/');

            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..")
                    throw new Hv86Exception("EACCES");

                foreach (char c in part)
                    if (c == '\\' || c == '\0' || char.IsControl(c))
                        throw new Hv86Exception("EACCES");
            }

            string full = Path.GetFullPath(Path.Combine(_root, string.Join("\\", parts)));

            if (!Inside(full)) throw new Hv86Exception("EACCES");

            return full;
        }

        /// <summary>Симлинк не должен выводить за пределы шары.</summary>
        private void Verify(string full)
        {
            if (!File.Exists(full) && !Directory.Exists(full)) throw new Hv86Exception("ENOENT");

            string real = full;

            try
            {
                FileSystemInfo info = Directory.Exists(full)
                    ? (FileSystemInfo)new DirectoryInfo(full)
                    : new FileInfo(full);

                FileSystemInfo target = info.ResolveLinkTarget(true);
                if (target != null) real = target.FullName;
            }
            catch
            {
                real = full;
            }

            if (!Inside(real)) throw new Hv86Exception("EACCES");
        }

        private void VerifyParent(string full)
        {
            string parent = Path.GetDirectoryName(full);

            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new Hv86Exception("ENOENT");

            Verify(parent);
        }

        private bool Inside(string path)
        {
            try
            {
                string value = Path.GetFullPath(path).TrimEnd('\\');
                string root = _rootReal.TrimEnd('\\');

                return string.Equals(value, root, StringComparison.OrdinalIgnoreCase) ||
                       value.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, object> StatOf(string full)
        {
            bool isDir = Directory.Exists(full);
            FileSystemInfo info = isDir
                ? (FileSystemInfo)new DirectoryInfo(full)
                : new FileInfo(full);

            if (!info.Exists) throw new Hv86Exception("ENOENT");

            long size = isDir ? 0 : ((FileInfo)info).Length;

            return Json.Obj(
                "dir", isDir,
                "size", size,
                "atimeMs", ToMillis(info.LastAccessTimeUtc),
                "mtimeMs", ToMillis(info.LastWriteTimeUtc),
                "ctimeMs", ToMillis(info.CreationTimeUtc),
                "nlink", 1,
                "mode", isDir ? 0x1FF : 0x1B6);
        }

        private static long ToMillis(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private Dictionary<string, object> Statfs()
        {
            try
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(_root));
                long bsize = 4096;
                long total = drive.TotalSize;
                long free = drive.TotalFreeSpace;
                long available = drive.AvailableFreeSpace;

                return Json.Obj(
                    "bsize", bsize,
                    "blocks", total / bsize,
                    "bfree", free / bsize,
                    "bavail", available / bsize,
                    "files", 0,
                    "ffree", 0);
            }
            catch
            {
                return Json.Obj("bsize", 4096, "blocks", 0, "bfree", 0, "bavail", 0, "files", 0, "ffree", 0);
            }
        }

        private static string CodeOf(Exception ex)
        {
            if (ex is FileNotFoundException) return "ENOENT";
            if (ex is DirectoryNotFoundException) return "ENOENT";
            if (ex is UnauthorizedAccessException) return "EACCES";
            if (ex is IOException)
            {
                int code = System.Runtime.InteropServices.Marshal.GetHRForException(ex) & 0xFFFF;

                switch (code)
                {
                    case 32: return "EACCES";     // sharing violation
                    case 80:
                    case 183: return "EEXIST";
                    case 145: return "ENOTEMPTY";
                    case 5: return "EACCES";
                    default: return "EIO";
                }
            }

            return "EIO";
        }
    }
}