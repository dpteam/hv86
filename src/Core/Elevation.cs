using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Hv86.Core
{
    /// <summary>Проверка и запрос прав администратора.</summary>
    public static class Elevation
    {
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Перезапуск себя с правами администратора. true - запрос отправлен.</summary>
        public static bool RestartElevated(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process.Start(psi);
                return true;
            }
            catch (Win32Exception)
            {
                // пользователь нажал «нет» в UAC
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn("ELEV", "перезапуск: " + ex.Message);
                return false;
            }
        }

        public static bool IsRunningAsSystem
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return identity.User != null &&
                               identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid);
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string CurrentUser
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                        return identity.Name;
                }
                catch
                {
                    return "?";
                }
            }
        }
    }
}