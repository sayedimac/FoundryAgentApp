using WebApp.Models;

namespace WebApp.Services;

public interface IShippingAgentService
{
    Task<string> GetResponseAsync(
        string prompt,
        IEnumerable<ChatMessage> history,
        CancellationToken cancellationToken = default);
}
