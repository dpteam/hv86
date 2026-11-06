using System;
using System.Collections.Generic;
using System.IO;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>Чтение/запись vm.json и операции над каталогами ВМ.</summary>
    public static class VmStore
    {
        /// <summary>Идентификатор: 8 hex-символов. Только hex - чтобы id нельзя было
        /// использовать как путь наружу.</summary>
        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length < 4 || id.Length > 32) return false;
            foreach (char c in id)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        public static bool Exists(string id)
        {
            return IsValidId(id) && File.Exists(Paths.VmFile(id));
        }

        public static List<VmDefinition> LoadAll()
        {
            List<VmDefinition> list = new List<VmDefinition>();

            try
            {
                if (!Directory.Exists(Paths.VmsDir)) return list;

                foreach (string dir in Directory.GetDirectories(Paths.VmsDir))
                {
                    string id = Path.GetFileName(dir);
                    if (!IsValidId(id)) continue;

                    VmDefinition vm = Load(id);
                    if (vm != null) list.Add(vm);
                }

                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Exception("VM", "список ВМ", ex);
            }

            return list;
        }

        public static VmDefinition Load(string id)
        {
            if (!IsValidId(id)) return null;

            Dictionary<string, object> data = Json.ReadFile(Paths.VmFile(id));
            if (data == null) return null;

            try
            {
                VmDefinition vm = VmDefinition.FromDict(data);
                if (vm == null) return null;

                vm.Id = id;
                if (string.IsNullOrEmpty(vm.Name)) vm.Name = id;
                return vm;
            }
            catch (Exception ex)
            {
                Log.Exception("VM", "разбор vm.json " + id, ex);
                return null;
            }
        }

        public static void Save(VmDefinition vm)
        {
            if (vm == null || !IsValidId(vm.Id)) throw new Hv86Exception("некорректный id ВМ");

            Paths.EnsureVmDirectories(vm.Id);
            Json.WriteFile(Paths.VmFile(vm.Id), vm.ToDict());
        }

        public static void Delete(string id, string name)
        {
            if (!IsValidId(id)) throw new Hv86Exception("некорректный id ВМ");

            string dir = Paths.VmDir(id);
            if (!Directory.Exists(dir)) return;

            AtomicFile.MoveToTrash(dir, "vm-" + (string.IsNullOrEmpty(name) ? id : name));
            Log.Info("VM", "ВМ " + id + " перемещена в корзину hv86");
        }

        /// <summary>Копия ВМ: файлы + новый id. Диски копируются целиком.</summary>
        public static VmDefinition Clone(string sourceId, string newName)
        {
            VmDefinition source = Load(sourceId);
            if (source == null) throw new Hv86Exception("ВМ не найдена: " + sourceId);

            string id = NewId();
            Paths.EnsureVmDirectories(id);

            string from = Paths.VmDir(sourceId);
            string to = Paths.VmDir(id);

            foreach (string sub in new[] { "disks", "media" })
            {
                string src = Path.Combine(from, sub);
                if (!Directory.Exists(src)) continue;

                string dst = Path.Combine(to, sub);
                Directory.CreateDirectory(dst);

                foreach (string file in Directory.GetFiles(src))
                    File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
            }

            VmDefinition copy = source.Clone(id, newName);
            Save(copy);
            return copy;
        }
    }
}