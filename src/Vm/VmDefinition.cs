using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>Политики перезапуска после падения.</summary>
    public static class RestartPolicy
    {
        public const string Always = "always";
        public const string OnFailure = "on-failure";
        public const string Never = "never";
    }

    /// <summary>
    /// Описание ВМ = содержимое vm.json.
    ///
    /// Диски и медиа хранятся относительными путями от каталога ВМ: так ВМ
    /// можно перенести/клонировать целиком, не переписывая файл.
    /// </summary>
    public sealed class VmDefinition
    {
        public string Id = "";
        public string Name = "";

        public int MemoryMb = 128;
        public int VgaMemoryMb = 8;

        /// <summary>web - v86 в WebView2. headless - зарезервировано под M5.</summary>
        public string Engine = "web";

        public string HdaFile = "disks/system.img";
        public string CdromFile = null;
        public string FdaFile = null;

        public string NetType = "virtio";
        public string NetMode = "nat";
        public string BootOrder = "auto";

        /// <summary>Проброс TCP: host-порт → порт в госте.</summary>
        public List<ForwardRule> Forwards = new List<ForwardRule>();

        /// <summary>Общая папка для гостя (9p). Только headless-движок.</summary>
        public string SharePath;
        public string ShareTag = "host9p";
        public bool ShareReadOnly = true;

        /// <summary>Политика исходящего трафика для wisp-релея.</summary>
        public string EgressMode = EgressPolicy.ModeAny;
        public List<string> EgressAllow = new List<string>();

        public bool Autostart;

        // Динамическая отказоустойчивость
        public string FtMode = "checkpoint";          // off | checkpoint | replica (позже)
        public int FtIntervalMs = 15000;
        public int FtKeep = 3;
        public string Restart = RestartPolicy.OnFailure;
        public int MaxAttempts = 5;
        public bool RestoreOnCrash = true;

        // Состояние (обновляет служба, не пользователь)
        public string State = "stopped";
        public int Restarts;
        public string LastError = "";
        public string CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        public string V86Version = AppInfo.V86Version;
        public string Notes = "";

        public string HdaPath { get { return Paths.Absolute(Id, HdaFile); } }
        public string CdromPath { get { return Paths.Absolute(Id, CdromFile); } }
        public string FdaPath { get { return Paths.Absolute(Id, FdaFile); } }

        /// <summary>Имя boot_order в число, которое понимает v86.</summary>
        public static int BootOrderValue(string name)
        {
            switch ((name ?? "").Trim().ToUpperInvariant())
            {
                case "CD_FLOPPY_HARDDISK": return 0x213;
                case "CD_HARDDISK_FLOPPY": return 0x123;
                case "FLOPPY_CD_HARDDISK": return 0x231;
                case "FLOPPY_HARDDISK_CD": return 0x321;
                case "HARDDISK_CD_FLOPPY": return 0x132;
                default: return 0;
            }
        }

        public bool IsHeadless { get { return Engine == "headless"; } }

        public bool HasHda { get { return !string.IsNullOrEmpty(HdaFile); } }
        public bool HasCdrom { get { return !string.IsNullOrEmpty(CdromFile); } }
        public bool HasFda { get { return !string.IsNullOrEmpty(FdaFile); } }

        public bool IsRunning
        {
            get { return State == "running" || State == "starting" || State == "paused" || State == "recovering"; }
        }

        // ------------------------------------------------------------------ JSON

        public Dictionary<string, object> ToDict()
        {
            Dictionary<string, object> ft = Json.Obj(
                "mode", FtMode,
                "interval_ms", FtIntervalMs,
                "keep", FtKeep,
                "restart", Restart,
                "max_attempts", MaxAttempts,
                "restore_on_crash", RestoreOnCrash);

            Dictionary<string, object> netBlock = Json.Obj(
                    "type", NetType,
                    "mode", NetMode);

                if (Forwards != null && Forwards.Count > 0)
                {
                    List<object> forwardList = new List<object>();
                    foreach (ForwardRule rule in Forwards) forwardList.Add(rule.ToDict());
                    netBlock["forwards"] = forwardList;
                }

                Dictionary<string, object> shareBlock = null;

                if (!string.IsNullOrEmpty(SharePath))
                {
                    shareBlock = Json.Obj(
                        "path", SharePath,
                        "tag", string.IsNullOrEmpty(ShareTag) ? "host9p" : ShareTag,
                        "readonly", ShareReadOnly);
                }

                if (EgressMode != EgressPolicy.ModeAny || (EgressAllow != null && EgressAllow.Count > 0))
                {
                    List<object> allow = new List<object>();
                    if (EgressAllow != null)
                        foreach (string item in EgressAllow) allow.Add(item);

                    netBlock["egress"] = Json.Obj("mode", EgressMode, "allow", allow);
                }

                return Json.Obj(
                    "id", Id,
                    "name", Name,
                    "memory_mb", MemoryMb,
                    "vga_memory_mb", VgaMemoryMb,
                    "engine", Engine,
                    "media", Json.Obj(
                        "hda", HdaFile,
                        "cdrom", CdromFile,
                        "fda", FdaFile),
                    "net", netBlock,
                "boot_order", BootOrder,
                "autostart", Autostart,
                "share", shareBlock,
                "ft", ft,
                "state", State,
                "restarts", Restarts,
                "last_error", LastError,
                "created_utc", CreatedUtc,
                "v86_version", V86Version,
                "notes", Notes);
        }

        public static VmDefinition FromDict(Dictionary<string, object> map)
        {
            if (map == null) return null;

            VmDefinition vm = new VmDefinition();
            vm.Id = Json.GetString(map, "id", "");
            vm.Name = Json.GetString(map, "name", vm.Id);
            vm.MemoryMb = Json.GetInt(map, "memory_mb", 128);
            vm.VgaMemoryMb = Json.GetInt(map, "vga_memory_mb", 8);
            vm.Engine = Json.GetString(map, "engine", "web");
            if (vm.Engine != "web" && vm.Engine != "headless") vm.Engine = "web";

            Dictionary<string, object> media = Json.GetObject(map, "media");
            if (media != null)
            {
                vm.HdaFile = Json.GetString(media, "hda", null);
                vm.CdromFile = Json.GetString(media, "cdrom", null);
                vm.FdaFile = Json.GetString(media, "fda", null);
            }

            Dictionary<string, object> net = Json.GetObject(map, "net");
            if (net != null)
            {
                vm.NetType = Json.GetString(net, "type", "virtio");
                vm.NetMode = Json.GetString(net, "mode", "nat");

                List<object> forwards = Json.GetList(net, "forwards");
                int dropped;
                vm.Forwards = ForwardRule.ParseList(forwards, out dropped);
                if (dropped > 0) Log.Warn("VM", "отброшено некорректных правил проброса: " + dropped);

                Dictionary<string, object> egress = Json.GetObject(net, "egress");
                if (egress != null)
                {
                    vm.EgressMode = EgressPolicy.NormalizeMode(Json.GetString(egress, "mode", EgressPolicy.ModeAny));

                    List<object> allow = Json.GetList(egress, "allow");
                    List<string> items = new List<string>();

                    if (allow != null)
                        foreach (object item in allow)
                            if (item is string text) items.Add(text);

                    List<EgressRule> rules = EgressPolicy.ParseRules(items, out dropped);
                    vm.EgressAllow = new List<string>();
                    foreach (EgressRule rule in rules) vm.EgressAllow.Add(rule.Text);
                }
            }

            vm.BootOrder = Json.GetString(map, "boot_order", "auto");
            vm.Autostart = Json.GetBool(map, "autostart", false);

            Dictionary<string, object> share = Json.GetObject(map, "share");
            if (share != null)
            {
                vm.SharePath = Json.GetString(share, "path", null);
                vm.ShareTag = Json.GetString(share, "tag", "host9p");
                vm.ShareReadOnly = Json.GetBool(share, "readonly", true);
            }

            Dictionary<string, object> ft = Json.GetObject(map, "ft");
            if (ft != null)
            {
                vm.FtMode = Json.GetString(ft, "mode", "checkpoint");
                vm.FtIntervalMs = Json.GetInt(ft, "interval_ms", 15000);
                vm.FtKeep = Json.GetInt(ft, "keep", 3);
                vm.Restart = Json.GetString(ft, "restart", RestartPolicy.OnFailure);
                vm.MaxAttempts = Json.GetInt(ft, "max_attempts", 5);
                vm.RestoreOnCrash = Json.GetBool(ft, "restore_on_crash", true);
            }

            vm.State = Json.GetString(map, "state", "stopped");
            vm.Restarts = Json.GetInt(map, "restarts", 0);
            vm.LastError = Json.GetString(map, "last_error", "");
            vm.CreatedUtc = Json.GetString(map, "created_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            vm.V86Version = Json.GetString(map, "v86_version", AppInfo.V86Version);
            vm.Notes = Json.GetString(map, "notes", "");

            return vm;
        }

        /// <summary>Короткая сводка для интерфейса.</summary>
        public Dictionary<string, object> ToView()
        {
            return Json.Obj(
                "id", Id,
                "name", Name,
                "state", State,
                "engine", Engine,
                "memory_mb", MemoryMb,
                "vga_memory_mb", VgaMemoryMb,
                "disk", DescribeMedia(HdaFile),
                "cdrom", DescribeMedia(CdromFile),
                "floppy", DescribeMedia(FdaFile),
                "autostart", Autostart,
                "net_mode", NetMode,
                "share_path", SharePath,
                "share_readonly", ShareReadOnly,
                "egress_mode", EgressMode,
                "egress_allow", ViewAllow(),
                "ft_mode", FtMode,
                "restart", Restart,
                "interval_ms", FtIntervalMs,
                "ft_keep", FtKeep,
                "max_attempts", MaxAttempts,
                "restore_on_crash", RestoreOnCrash,
                "forwards", ForwardsView(),
                "restarts", Restarts,
                "last_error", LastError,
                "created_utc", CreatedUtc,
                "v86_version", V86Version);
        }

        private static string DescribeMedia(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            try { return Path.GetFileName(relative); }
            catch { return relative; }
        }

        private List<object> ForwardsView()
        {
            List<object> list = new List<object>();
            if (Forwards == null) return list;

            foreach (ForwardRule rule in Forwards) list.Add(rule.ToDict());
            return list;
        }

        private List<object> ViewAllow()
        {
            List<object> list = new List<object>();
            if (EgressAllow == null) return list;

            foreach (string item in EgressAllow) list.Add(item);
            return list;
        }

        public VmDefinition Clone(string newId, string newName)
        {
            VmDefinition copy = FromDict(Json.ParseObject(Json.Stringify(ToDict())));
            copy.Id = newId;
            copy.Name = newName;
            copy.State = "stopped";
            copy.Restarts = 0;
            copy.LastError = "";
            copy.CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            return copy;
        }
    }
}