using System;
using System.IO;
using Hv86.Core;
using Hv86.Vm;
using Hv86.VmHost;

namespace Hv86.App
{
    /// <summary>
    /// `hv86.exe --smoke &lt;образ&gt;`: сквозной самотест без службы и без рук.
    ///
    /// Создаёт временную ВМ во временном каталоге, поднимает vmhost, ждёт
    /// загрузку гостя, снимает state, перезапускает ВМ из этого state и
    /// проверяет, что она снова исполняет инструкции.
    ///
    /// Проверяется сразу всё, что легко сломать: WebView2 в нужной сессии,
    /// v86, отдача образов по Range, приём state кусками, восстановление.
    /// </summary>
    public static class SmokeTest
    {
        public static int Run(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath) || !System.IO.File.Exists(imagePath))
            {
                Console.WriteLine("usage: hv86.exe --smoke <floppy-or-hdd-image>");
                Console.WriteLine("пример: hv86.exe --smoke freedos722.img");
                return 2;
            }

            string keepRoot = Environment.GetEnvironmentVariable("HV86_SMOKE_ROOT");
            bool keep = !string.IsNullOrEmpty(keepRoot);

            string root = keep
                ? keepRoot
                : Path.Combine(Path.GetTempPath(), "hv86-smoke-" + DateTime.Now.ToString("HHmmss"));

            // временный корень: не трогаем ни ProgramData, ни реальные ВМ
            Paths.ForceRoot(root);
            Paths.EnsureDirectories();

            string id = "5a0ce001";

            try
            {
                VmDefinition vm = new VmDefinition
                {
                    Id = id,
                    Name = "smoke",
                    MemoryMb = 32,
                    VgaMemoryMb = 4,
                    BootOrder = "FLOPPY_CD_HARDDISK",
                    HdaFile = null,
                    State = "running"
                };

                Paths.EnsureVmDirectories(id);

                string target = Path.Combine(Paths.VmMediaDir(id), "floppy.img");
                File.Copy(imagePath, target, true);
                vm.FdaFile = Paths.Relative(id, target);

                VmStore.Save(vm);

                Console.WriteLine("hv86 smoke test");
                Console.WriteLine("  image: " + imagePath);
                Console.WriteLine("  root:  " + Paths.Root);

                int code = new VmHostRunner(id, false, null, true).Run();

                Console.WriteLine(code == 0 ? "SMOKE: OK" : "SMOKE: FAILED (" + code + ")");
                return code;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SMOKE: FAILED - " + ex.Message);
                Log.Exception("SMOKE", "исключение", ex);
                return 1;
            }
            finally
            {
                try
                {
                    if (!keep && Directory.Exists(root))
                        Directory.Delete(root, true);
                    else if (keep)
                        Console.WriteLine("  artifacts kept: " + root);
                }
                catch
                {
                    // каталог может быть занят - не страшно, он во временной папке
                }
            }
        }
    }
}