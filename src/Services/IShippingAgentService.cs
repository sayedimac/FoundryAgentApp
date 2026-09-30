using WebApp.Models;

namespace WebApp.Services;

public interface IShippingAgentService
{
    Task<string> GetResponseAsync(
        string prompt,
        IEnumerable<ChatMessage> history,
        CancellationToken cancellationToken = default);

    Task<ShippingKnowledgeItem> CreateKnowledgeItemAsync(
        CreateShippingKnowledgeItem request,
        CancellationToken cancellationToken = default);

    Task<ShippingKnowledgePage> GetKnowledgeItemsAsync(
        string? continuationToken,
        int pageSize,
        CancellationToken cancellationToken = default);
}
