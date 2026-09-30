using Microsoft.AspNetCore.Mvc;
using WebApp.Models;
using WebApp.Services;

namespace WebApp.Controllers;

[Route("knowledge")]
public sealed class ShippingKnowledgeController(IShippingAgentService shippingAgentService) : Controller
{
    [HttpGet("")]
    public IActionResult Index()
    {
        return RedirectToAction(nameof(Items));
    }

    [HttpGet("add")]
    public IActionResult Add()
    {
        return View(new CreateShippingKnowledgeViewModel());
    }

    [HttpPost("add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(
        CreateShippingKnowledgeViewModel model,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var tags = model.TagsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        var item = await shippingAgentService.CreateKnowledgeItemAsync(
            new CreateShippingKnowledgeItem
            {
                DocumentId = model.DocumentId,
                Content = model.Content,
                Source = model.Source,
                Category = model.Category,
                Tags = tags
            },
            cancellationToken);

        TempData["KnowledgeSuccess"] = $"Created item {item.Id} with {item.Embedding.Count} embedding values.";
        return RedirectToAction(nameof(Items));
    }

    [HttpGet("items")]
    public async Task<IActionResult> Items(
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var page = await shippingAgentService.GetKnowledgeItemsAsync(
            continuationToken,
            pageSize: 25,
            cancellationToken);
        return View(page);
    }
}
