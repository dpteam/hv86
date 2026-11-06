using System;
using System.Reflection;

namespace Hv86.Core
{
    /// <summary>Сведения о продукте: имя, версия, версия v86.</summary>
    public static class AppInfo
    {
        public const string Name = "hv86";
        public const string ProductName = "hv86 Hypervisor";
        public const string ServiceName = "hv86";
        public const string ServiceDisplayName = "hv86 Hypervisor";

        /// <summary>Версия v86, ассеты которой вшиты в exe (см. hv86.csproj).</summary>
        public const string V86Version = "0.5.473";

        public static string Version
        {
            get
            {
                try
                {
                    Assembly asm = typeof(AppInfo).Assembly;
                    AssemblyInformationalVersionAttribute info =
                        (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                            asm, typeof(AssemblyInformationalVersionAttribute));
                    if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                        return info.InformationalVersion;
                    return asm.GetName().Version.ToString(3);
                }
                catch
                {
                    return "0.0.0";
                }
            }
        }

        public static string Summary()
        {
            return ProductName + " " + Version + " (v86 " + V86Version + ")";
        }
    }
}