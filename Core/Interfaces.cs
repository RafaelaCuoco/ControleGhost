using System.Threading;
using System.Threading.Tasks;

namespace ControleGhost.Core
{
    public interface IJsonToXmlConverter
    {
        string Convert(string json);
    }

    public interface IBandwidthThrottler
    {
        Task ThrottleAsync(int bytesProcessed, CancellationToken ct);
    }
}
