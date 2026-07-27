namespace AgentApp.PlaywrightTests;

/// <summary>
/// Tests that verify the chat header area and agent switching behavior.
/// </summary>
[TestFixture]
public class ChatHeaderTests : PlaywrightTestBase
{
    [Test]
    public async Task ChatHeader_ShowsCurrentAgentInfo()
    {
        await Page.GotoAsync(BaseUrl);

        var avatar = Page.Locator("#agentAvatar");
        await Expect(avatar).ToBeVisibleAsync();
        await Expect(avatar).ToHaveTextAsync("C");

        var name = Page.Locator("#agentName");
        await Expect(name).ToHaveTextAsync("Code");

        var description = Page.Locator("#agentDescription");
        await Expect(description).ToContainTextAsync("Coding");
    }

    [Test]
    public async Task ChatHeader_ShowsClearChatButton()
    {
        await Page.GotoAsync(BaseUrl);

        var clearBtn = Page.Locator("#clearChatBtn");
        await Expect(clearBtn).ToBeVisibleAsync();
    }

    [Test]
    public async Task ChatHeader_SwitchingAgentUpdatesHeader()
    {
        await Page.GotoAsync(BaseUrl);

        // Switch to Travel agent
        await Page.Locator("#agentSelect").SelectOptionAsync("Travel");

        // Wait for the agent header to update
        var name = Page.Locator("#agentName");
        await Expect(name).ToHaveTextAsync("Travel", new() { Timeout = 5000 });

        var avatar = Page.Locator("#agentAvatar");
        await Expect(avatar).ToHaveTextAsync("T");
    }

    [Test]
    public async Task ChatHeader_SwitchingBackToCodeAgent()
    {
        await Page.GotoAsync(BaseUrl);

        // Switch to Travel, then back to Code
        await Page.Locator("#agentSelect").SelectOptionAsync("Travel");
        await Expect(Page.Locator("#agentName")).ToHaveTextAsync("Travel", new() { Timeout = 5000 });

        await Page.Locator("#agentSelect").SelectOptionAsync("Code");
        await Expect(Page.Locator("#agentName")).ToHaveTextAsync("Code", new() { Timeout = 5000 });
        await Expect(Page.Locator("#agentAvatar")).ToHaveTextAsync("C");
    }
}
