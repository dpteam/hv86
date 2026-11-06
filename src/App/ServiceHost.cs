using System;
using System.Runtime.InteropServices;
using System.Threading;
using Hv86.Core;

namespace Hv86.App
{
    /// <summary>
    /// Тело службы Windows: то, что вызывает SCM.
    ///
    /// Порядок строго такой:
    ///   1. StartServiceCtrlDispatcher - сразу, до любой инициализации;
    ///   2. ServiceMain - регистрируем обработчик и рапортуем STARTING;
    ///   3. работа - движок (VmManager + pipe-сервер);
    ///   4. STOPPED - иначе SCM будет считать службу висящей.
    ///
    /// Окно и WebView2 здесь создать нельзя (session 0), поэтому вся
    /// визуальная часть - в отдельных процессах vmhost, которые служба
    /// запускает в сессии пользователя.
    /// </summary>
    public static class ServiceHost
    {
        public const int ServiceWin32OwnProcess = 0x00000010;
        public const int ServiceAcceptStop = 0x00000001;
        public const int ServiceAcceptShutdown = 0x00000004;

        public const int ServiceStopped = 0x00000001;
        public const int ServiceStartPending = 0x00000002;
        public const int ServiceStopPending = 0x00000003;
        public const int ServiceRunning = 0x00000004;

        public const int ServiceControlStop = 0x00000001;
        public const int ServiceControlInterrogate = 0x00000004;
        public const int ServiceControlShutdown = 0x00000005;
        public const int ServiceControlPreshutdown = 0x0000000F;

        public const int ErrorFailedServiceControllerConnect = 1063;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_TABLE_ENTRY
        {
            [MarshalAs(UnmanagedType.LPStr)]
            public string lpServiceName;

            public IntPtr lpServiceProc;
        }

        private delegate void ServiceMainFunction(int argc, IntPtr argv);
        private delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool StartServiceCtrlDispatcherW(IntPtr serviceTable);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerEx handler, IntPtr context);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

        // делегаты обязаны жить всё время работы службы: соберёт GC - SCM
        // вызовет освобождённую память и упадёт весь процесс
        private static ServiceMainFunction _mainCallback;
        private static HandlerEx _handlerCallback;

        private static IntPtr _statusHandle = IntPtr.Zero;
        private static readonly ManualResetEvent StopEvent = new ManualResetEvent(false);
        private static bool _stopping;

        public static int Run(string serviceName)
        {
            Log.Info("SVC", "запуск режима службы: " + serviceName);

            _mainCallback = ServiceMain;
            _handlerCallback = HandleControl;

            IntPtr namePtr = Marshal.StringToHGlobalAnsi(serviceName);
            IntPtr procPtr = Marshal.GetFunctionPointerForDelegate(_mainCallback);

            int entrySize = Marshal.SizeOf(typeof(SERVICE_TABLE_ENTRY));
            IntPtr table = Marshal.AllocHGlobal(entrySize * 2);

            try
            {
                SERVICE_TABLE_ENTRY entry = new SERVICE_TABLE_ENTRY
                {
                    lpServiceName = serviceName,
                    lpServiceProc = procPtr
                };

                Marshal.StructureToPtr(entry, table, false);

                SERVICE_TABLE_ENTRY terminator = new SERVICE_TABLE_ENTRY
                {
                    lpServiceName = null,
                    lpServiceProc = IntPtr.Zero
                };

                Marshal.StructureToPtr(terminator, new IntPtr(table.ToInt64() + entrySize), false);

                bool ok = StartServiceCtrlDispatcherW(table);

                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();

                    if (error == ErrorFailedServiceControllerConnect)
                    {
                        Log.Error("SVC", "это режим службы: запускать через SCM, а не из консоли");
                        Console.WriteLine("This is the service body: it is started by SCM, not by hand.");
                        Console.WriteLine("Install and start: hv86.exe --service install|start");
                        return 5;
                    }

                    Log.Error("SVC", "StartServiceCtrlDispatcher: код " + error);
                    return 6;
                }

                Log.Info("SVC", "служба остановлена штатно");
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(table);
                Marshal.FreeHGlobal(namePtr);
            }
        }

        private static void ServiceMain(int argc, IntPtr argv)
        {
            _statusHandle = RegisterServiceCtrlHandlerExW(AppInfo.ServiceName, _handlerCallback, IntPtr.Zero);

            if (_statusHandle == IntPtr.Zero)
            {
                Log.Error("SVC", "RegisterServiceCtrlHandlerEx не удался: " + Marshal.GetLastWin32Error());
                return;
            }

            Report(ServiceStartPending, 0, 5000);

            try
            {
                ServiceRunner runner = new ServiceRunner();
                runner.Start();

                Report(ServiceRunning, 0, 0);
                Log.Info("SVC", "служба работает");

                StopEvent.WaitOne();

                Report(ServiceStopPending, 0, 30000);

                Log.Info("SVC", "останавливаю движок");
                runner.Stop();

                Report(ServiceStopped, 0, 0);
            }
            catch (Exception ex)
            {
                Log.Exception("SVC", "работа службы", ex);

                // 1064 = исключение в службе; без этого SCM покажет «запущена»,
                // хотя внутри уже всё упало
                Report(ServiceStopped, 1064, 0);
            }
        }

        private static int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
        {
            switch (control)
            {
                case ServiceControlStop:
                case ServiceControlShutdown:
                case ServiceControlPreshutdown:
                    if (_stopping) return 0;
                    _stopping = true;

                    Log.Info("SVC", "получен запрос остановки: " + control);
                    StopEvent.Set();
                    return 0;

                case ServiceControlInterrogate:
                    Report(_stopping ? ServiceStopPending : ServiceRunning, 0, 0);
                    return 0;
            }

            return 0;
        }

        private static void Report(int state, int exitCode, int waitHint)
        {
            if (_statusHandle == IntPtr.Zero) return;

            SERVICE_STATUS status = new SERVICE_STATUS
            {
                dwServiceType = ServiceWin32OwnProcess,
                dwCurrentState = state,
                dwControlsAccepted = ServiceAcceptStop | ServiceAcceptShutdown,
                dwWin32ExitCode = exitCode,
                dwServiceSpecificExitCode = 0,
                dwCheckPoint = 0,
                dwWaitHint = waitHint
            };

            if (state == ServiceStartPending || state == ServiceStopPending)
                status.dwCheckPoint = (int)(DateTime.UtcNow.Ticks % 1000);

            SetServiceStatus(_statusHandle, ref status);
        }
    }
}