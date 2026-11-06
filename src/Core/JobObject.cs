using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hv86.Core
{
    /// <summary>
    /// Job Object с KILL_ON_JOB_CLOSE: процессы ВМ умирают вместе с тем, кто
    /// их породил. Без этого после падения службы в памяти остаются висящие
    /// vmhost'ы с открытыми дисками - и следующая ВМ их уже не откроет.
    /// </summary>
    public sealed class JobObject : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private IntPtr _handle = IntPtr.Zero;

        public JobObject(string name)
        {
            try
            {
                _handle = CreateJobObject(IntPtr.Zero, name);
                if (_handle == IntPtr.Zero)
                {
                    Log.Warn("JOB", "CreateJobObject: код " + Marshal.GetLastWin32Error());
                    return;
                }

                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr buffer = Marshal.AllocHGlobal(size);

                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                        Log.Warn("JOB", "SetInformationJobObject: код " + Marshal.GetLastWin32Error());
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("JOB", ex.Message);
            }
        }

        public bool Assign(Process process)
        {
            if (process == null || _handle == IntPtr.Zero) return false;
            try
            {
                if (AssignProcessToJobObject(_handle, process.Handle)) return true;
                Log.Warn("JOB", "AssignProcessToJobObject: код " + Marshal.GetLastWin32Error());
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn("JOB", "назначение процесса: " + ex.Message);
                return false;
            }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            try { CloseHandle(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }
}