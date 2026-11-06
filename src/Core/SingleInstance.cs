using System;
using System.Threading;

namespace Hv86.Core
{
    /// <summary>
    /// Один интерфейс на пользователя. Служба и процессы ВМ этим не пользуются:
    /// у них свои имена (и своя область видимости), иначе второй хост не
    /// запустится, пока жив первый.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private Mutex _mutex;
        private readonly string _name;

        public SingleInstance(string name)
        {
            _name = name;
        }

        public bool TryAcquire(int waitMs)
        {
            try
            {
                _mutex = new Mutex(false, _name);
                try
                {
                    if (waitMs <= 0) return _mutex.WaitOne(0);
                    return _mutex.WaitOne(waitMs);
                }
                catch (AbandonedMutexException)
                {
                    // предыдущий экземпляр умер, не освободив мьютекс - это наш
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SINGLE", "мьютекс " + _name + ": " + ex.Message);
                return true;
            }
        }

        public void Dispose()
        {
            if (_mutex == null) return;
            try { _mutex.ReleaseMutex(); } catch { }
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
        }
    }
}