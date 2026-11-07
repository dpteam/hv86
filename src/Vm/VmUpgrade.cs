using System;
using System.Collections.Generic;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Перенос ВМ на другой движок v86.
    ///
    /// state v86 между версиями несовместим, поэтому «обновить движок» - это не
    /// замена цифры в vm.json, а осознанная операция: диски остаются как есть,
    /// а снапшоты и чекпоинты старой версии уходят в корзину. Иначе получилось
    /// бы худшее из возможного - ВМ грузится новым эмулятором и падает при
    /// первом же откате, причём непонятно почему.
    ///
    /// Вызывать только у остановленной ВМ: state живого гостя мигрировать некуда.
    /// </summary>
    public static class VmUpgrade
    {
        /// <summary>
        /// Что будет потеряно при переносе. Нужно, чтобы UI мог спросить
        /// подтверждение, а не угадывать.
        /// </summary>
        public static Dictionary<string, object> Plan(VmDefinition vm)
        {
            if (vm == null) throw new Hv86Exception("ВМ не найдена");

            return Json.Obj(
                "from", vm.V86Version,
                "to", AppInfo.V86Version,
                "needed", NeedsMigration(vm),
                "snapshots", SafeSnapshotCount(vm),
                "checkpoints", CheckpointStore.RingCount(vm));
        }

        public static bool NeedsMigration(VmDefinition vm)
        {
            return vm != null && !string.Equals(vm.V86Version, AppInfo.V86Version, StringComparison.Ordinal);
        }

        /// <summary>Готовый текст для UI: что случилось и что делать.</summary>
        public static string Hint(VmDefinition vm)
        {
            string from = vm == null ? "?" : vm.V86Version;

            return "ВМ сделана на v86 " + from + ", а встроен " + AppInfo.V86Version +
                   ". Состояние между версиями несовместимо: перенесите ВМ на встроенный " +
                   "движок (hv86 --cli migrate " + (vm == null ? "<vm>" : vm.Name) + ") — " +
                   "диски сохранятся, снапшоты старой версии будут удалены.";
        }

        /// <summary>
        /// Переносит ВМ на встроенный движок. Возвращает сводку для UI и лога.
        /// </summary>
        public static Dictionary<string, object> Migrate(VmDefinition vm)
        {
            if (vm == null) throw new Hv86Exception("ВМ не найдена");

            string from = vm.V86Version;
            int snapshots = 0;
            int checkpoints = CheckpointStore.RingCount(vm);

            List<Dictionary<string, object>> all = CheckpointStore.List(vm.Id);

            foreach (Dictionary<string, object> item in all)
            {
                string id = Json.GetString(item, "id", "");
                if (id.Length == 0) continue;

                try
                {
                    // по правилам данных - в корзину, а не Directory.Delete
                    CheckpointStore.Delete(vm, id);
                    snapshots++;
                }
                catch (Exception ex)
                {
                    Log.Warn("UPG", "снапшот " + id + " не удалён: " + ex.Message);
                }
            }

            CheckpointStore.ClearRing(vm);

            vm.V86Version = AppInfo.V86Version;
            vm.State = "stopped";
            vm.Restarts = 0;
            vm.LastError = "";

            Log.Info("UPG", "ВМ " + vm.Id + " перенесена с v86 " + from + " на " + AppInfo.V86Version +
                            " (снапшотов убрано: " + snapshots + ", чекпоинтов: " + checkpoints + ")");

            return Json.Obj(
                "from", from,
                "to", AppInfo.V86Version,
                "snapshots", snapshots,
                "checkpoints", checkpoints);
        }

        private static int SafeSnapshotCount(VmDefinition vm)
        {
            try { return CheckpointStore.List(vm.Id).Count; }
            catch { return 0; }
        }
    }
}