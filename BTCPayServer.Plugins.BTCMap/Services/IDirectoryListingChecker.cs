using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BTCMap.Services;

public interface IDirectoryListingChecker
{
    Task<DirectoryEntry> FindByUrl(string url);
}
