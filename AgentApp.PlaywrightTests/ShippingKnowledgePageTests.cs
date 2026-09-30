using Microsoft.Playwright;

namespace AgentApp.PlaywrightTests;

[TestFixture]
public class ShippingKnowledgePageTests : PlaywrightTestBase
{
    [Test]
    public async Task AddPage_ShowsEmbeddingForm()
    {
        await Page.GotoAsync($"{BaseUrl}/knowledge/add");

        await Expect(Page).ToHaveTitleAsync("Add item - Shipping Knowledge");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Add knowledge item" })).ToBeVisibleAsync();
        await Expect(Page.Locator("#Content")).ToBeVisibleAsync();
        await Expect(Page.Locator("#DocumentId")).ToBeVisibleAsync();
        await Expect(Page.Locator("#Source")).ToHaveValueAsync("knowledge-manager");
        await Expect(Page.Locator("#Category")).ToHaveValueAsync("shipping");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Create embedding and save" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task ChatPage_LinksToKnowledgeManagement()
    {
        await Page.GotoAsync(BaseUrl);

        var link = Page.GetByRole(AriaRole.Link, new() { Name = "Manage shipping knowledge" });
        await Expect(link).ToHaveAttributeAsync("href", "/knowledge/items");
    }
}
