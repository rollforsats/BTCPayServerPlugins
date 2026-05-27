using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Models;

namespace BTCPayServer.Plugins.BTCMap.Services;

public interface IPluginBuilderApiClient
{
    Task<BtcMapSubmitResponse> SubmitAsync(BtcMapSubmitRequest request);
}
