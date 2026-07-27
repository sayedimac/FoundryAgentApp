namespace AgentApp.PlaywrightTests;

/// <summary>
/// Tests that verify the sidebar, agent selector, and navigation elements work correctly.
/// </summary>
[TestFixture]
public class SidebarTests : PlaywrightTestBase
{
    [Test]
    public async Task Sidebar_ShowsAIChatHeader()
    {
        await Page.GotoAsync(BaseUrl);

        var header = Page.Locator(".sidebar-header h2");
        await Expect(header).ToHaveTextAsync("AI Chat");
    }

    [Test]
    public async Task Sidebar_ShowsNewChatButton()
    {
        await Page.GotoAsync(BaseUrl);

        var btn = Page.Locator("#newChatBtn");
        await Expect(btn).ToBeVisibleAsync();
    }

    [Test]
    public async Task Sidebar_ShowsDefaultConversation()
    {
        await Page.GotoAsync(BaseUrl);

        var conversation = Page.Locator(".conversation-item");
        await Expect(conversation).ToBeVisibleAsync();
        await Expect(conversation).ToContainTextAsync("New Conversation");
    }

    [Test]
    public async Task Sidebar_AgentSelectorHasExpectedOptions()
    {
        await Page.GotoAsync(BaseUrl);

        var select = Page.Locator("#agentSelect");
        await Expect(select).ToBeVisibleAsync();

        var options = select.Locator("option");
        await Expect(options).ToHaveCountAsync(2);
        await Expect(options.Nth(0)).ToHaveTextAsync("Code - Programming + GitHub tools");
        await Expect(options.Nth(1)).ToHaveTextAsync("Travel - Travel planning assistant");
    }

    [Test]
    public async Task Sidebar_CodeAgentIsSelectedByDefault()
    {
        await Page.GotoAsync(BaseUrl);

        var select = Page.Locator("#agentSelect");
        await Expect(select).ToHaveValueAsync("Code");
    }
}
