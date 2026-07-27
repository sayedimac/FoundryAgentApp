namespace AgentApp.PlaywrightTests;

/// <summary>
/// Tests that verify the home page loads correctly with all expected UI elements.
/// </summary>
[TestFixture]
public class HomePageTests : PlaywrightTestBase
{
    [Test]
    public async Task HomePage_LoadsWithCorrectTitle()
    {
        await Page.GotoAsync(BaseUrl);

        await Expect(Page).ToHaveTitleAsync("AI Chat - AI Chat");
    }

    [Test]
    public async Task HomePage_ShowsWelcomeMessage()
    {
        await Page.GotoAsync(BaseUrl);

        var welcomeHeading = Page.Locator(".welcome-message h1");
        await Expect(welcomeHeading).ToBeVisibleAsync();
        await Expect(welcomeHeading).ToHaveTextAsync("Welcome to AI Chat");
    }

    [Test]
    public async Task HomePage_ShowsWelcomeDescription()
    {
        await Page.GotoAsync(BaseUrl);

        var desc = Page.Locator(".welcome-message p");
        await Expect(desc).ToContainTextAsync("Select an agent and start a conversation");
    }

    [Test]
    public async Task HomePage_ShowsAllFeatures()
    {
        await Page.GotoAsync(BaseUrl);

        var features = Page.Locator(".feature");
        await Expect(features).ToHaveCountAsync(4);

        await Expect(Page.Locator(".feature >> text=Streaming Responses")).ToBeVisibleAsync();
        await Expect(Page.Locator(".feature >> text=Multiple Agents")).ToBeVisibleAsync();
        await Expect(Page.Locator(".feature >> text=Tool Calling")).ToBeVisibleAsync();
        await Expect(Page.Locator(".feature >> text=Markdown Support")).ToBeVisibleAsync();
    }

    [Test]
    public async Task HomePage_ShowsPoweredByFooter()
    {
        await Page.GotoAsync(BaseUrl);

        var hint = Page.Locator(".input-hint");
        await Expect(hint).ToContainTextAsync("Powered by Azure AI Foundry");
    }
}
