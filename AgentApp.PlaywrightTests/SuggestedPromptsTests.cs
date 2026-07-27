namespace AgentApp.PlaywrightTests;

/// <summary>
/// Tests that verify the suggested prompts section displays correctly
/// and that clicking a prompt populates the input field.
/// </summary>
[TestFixture]
public class SuggestedPromptsTests : PlaywrightTestBase
{
    [Test]
    public async Task SuggestedPrompts_SectionIsVisible()
    {
        await Page.GotoAsync(BaseUrl);

        var section = Page.Locator("#suggestedPrompts");
        await Expect(section).ToBeVisibleAsync();
    }

    [Test]
    public async Task SuggestedPrompts_ShowsTryAskingTitle()
    {
        await Page.GotoAsync(BaseUrl);

        var title = Page.Locator(".suggested-prompts-title");
        await Expect(title).ToHaveTextAsync("Try asking...");
    }

    [Test]
    public async Task SuggestedPrompts_ShowsCodeAgentPromptsByDefault()
    {
        await Page.GotoAsync(BaseUrl);

        // Wait for SignalR to connect and prompts to render
        var promptsList = Page.Locator("#suggestedPromptsList .suggested-prompt");
        await Expect(promptsList.First).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Code agent prompts should be visible
        await Expect(Page.Locator("#suggestedPromptsList >> text=Search for open issues")).ToBeVisibleAsync();
    }

    [Test]
    public async Task SuggestedPrompts_ChangeWhenAgentSwitched()
    {
        await Page.GotoAsync(BaseUrl);

        // Wait for Code agent prompts to load
        var promptsList = Page.Locator("#suggestedPromptsList .suggested-prompt");
        await Expect(promptsList.First).ToBeVisibleAsync(new() { Timeout = 10000 });

        // Switch to Travel agent
        await Page.Locator("#agentSelect").SelectOptionAsync("Travel");

        // Wait for Travel-specific prompts
        await Expect(Page.Locator("#suggestedPromptsList >> text=Tokyo")).ToBeVisibleAsync(new() { Timeout = 5000 });
    }
}
