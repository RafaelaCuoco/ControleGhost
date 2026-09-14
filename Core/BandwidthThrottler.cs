using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ControleGhost.Core
{
    /// <summary>
    /// Limitador de largura de banda que previne picos de I/O de rede e disco.
    /// </summary>
    public class BandwidthThrottler : IBandwidthThrottler
    {
        private readonly long _maxBytesPerSecond;
        private long _bytesProcessedInWindow;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _lock = new();

        public BandwidthThrottler(int maxMegabytesPerSecond)
        {
            _maxBytesPerSecond = maxMegabytesPerSecond * 1024L * 1024L;
        }

        public async Task ThrottleAsync(int bytesProcessed, CancellationToken ct)
        {
            lock (_lock)
            {
                _bytesProcessedInWindow += bytesProcessed;
            }

            // Se atingimos o limite de bytes no intervalo de 1 segundo
            if (_bytesProcessedInWindow >= _maxBytesPerSecond)
            {
                long elapsedMs = _stopwatch.ElapsedMilliseconds;
                if (elapsedMs < 1000)
                {
                    // Calcula o tempo que falta para completar 1 segundo
                    int delayMs = 1000 - (int)elapsedMs;
                    
                    // Task.Delay libera a thread atual de volta para o ThreadPool,
                    // permitindo que o SO e a aplicação respirem sem consumir CPU ativa (idle).
                    await Task.Delay(delayMs, ct); 
                }

                lock (_lock)
                {
                    _bytesProcessedInWindow = 0;
                    _stopwatch.Restart();
                }
            }
        }
    }
}
