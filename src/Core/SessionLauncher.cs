using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Hv86.Core
{
    /// <summary>
    /// Запуск процесса в интерактивной сессии пользователя.
    ///
    /// Зачем: WebView2 (а значит и vmhost) не работает в сессии 0, где живёт
    /// служба, - это ограничение Chromium/WebView2, а не наше. Поэтому служба
    /// (LocalSystem) находит активную консольную сессию и запускает vmhost
    /// с токеном вошедшего пользователя.
    ///
    /// Если процесс запущен не из службы (отладка, --smoke), запускаем обычным
    /// Process.Start - он и так в нужной сессии.
    /// </summary>
    public static class SessionLauncher
    {
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint MAXIMUM_ALLOWED = 0x02000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
            IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(IntPtr token, string applicationName, string commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
            IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string applicationName,
            string commandLine, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static int ActiveConsoleSessionId
        {
            get
            {
                uint id = WTSGetActiveConsoleSessionId();
                return id == 0xFFFFFFFF ? -1 : (int)id;
            }
        }

        public static bool InServiceSession
        {
            get { return Process.GetCurrentProcess().SessionId == 0; }
        }

        /// <summary>
        /// Запускает процесс там, где его увидит пользователь.
        /// Возвращает null и текст ошибки, если не получилось.
        /// </summary>
        public static Process Start(string exePath, string arguments, out string error)
        {
            error = null;

            try
            {
                if (!InServiceSession && !Elevation.IsRunningAsSystem)
                {
                    Process process = Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    if (process == null) error = "Process.Start вернул null";
                    return process;
                }

                int sessionId = ActiveConsoleSessionId;
                if (sessionId <= 0)
                {
                    error = "нет активной пользовательской сессии (никто не вошёл в систему)";
                    return null;
                }

                IntPtr userToken = IntPtr.Zero;
                IntPtr primaryToken = IntPtr.Zero;
                IntPtr environment = IntPtr.Zero;

                try
                {
                    if (!WTSQueryUserToken((uint)sessionId, out userToken))
                    {
                        error = "WTSQueryUserToken: код " + Marshal.GetLastWin32Error();
                        return null;
                    }

                    if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation,
                            TokenPrimary, out primaryToken))
                    {
                        error = "DuplicateTokenEx: код " + Marshal.GetLastWin32Error();
                        return null;
                    }

                    CreateEnvironmentBlock(out environment, primaryToken, false);

                    STARTUPINFO startup = new STARTUPINFO();
                    startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                    startup.lpDesktop = "winsta0\\default";

                    string commandLine = "\"" + exePath + "\" " + (arguments ?? string.Empty);
                    string workingDir = System.IO.Path.GetDirectoryName(exePath);
                    PROCESS_INFORMATION info;

                    bool ok = CreateProcessAsUser(primaryToken, null, commandLine, IntPtr.Zero, IntPtr.Zero,
                        false, CREATE_UNICODE_ENVIRONMENT, environment, workingDir, ref startup, out info);

                    if (!ok)
                    {
                        int code = Marshal.GetLastWin32Error();

                        // 1314 = не хватает привилегий; пробуем вариант «с чужим токеном»
                        ok = CreateProcessWithTokenW(primaryToken, 0, null, commandLine, CREATE_UNICODE_ENVIRONMENT,
                            environment, workingDir, ref startup, out info);

                        if (!ok)
                        {
                            error = "CreateProcessAsUser: код " + code +
                                    ", CreateProcessWithTokenW: код " + Marshal.GetLastWin32Error();
                            return null;
                        }
                    }

                    CloseHandle(info.hThread);

                    try
                    {
                        return Process.GetProcessById(info.dwProcessId);
                    }
                    finally
                    {
                        CloseHandle(info.hProcess);
                    }
                }
                finally
                {
                    if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                    if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
                    if (userToken != IntPtr.Zero) CloseHandle(userToken);
                }
            }
            catch (Win32Exception ex)
            {
                error = ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        public static string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("session=").Append(Process.GetCurrentProcess().SessionId);
            sb.Append(", active=").Append(ActiveConsoleSessionId);
            sb.Append(", user=").Append(Elevation.CurrentUser);
            return sb.ToString();
        }
    }
}