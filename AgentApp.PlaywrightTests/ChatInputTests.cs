namespace AgentApp.PlaywrightTests;

/// <summary>
/// Tests that verify the chat input area, send button, and form behavior.
/// </summary>
[TestFixture]
public class ChatInputTests : PlaywrightTestBase
{
    [Test]
    public async Task ChatInput_TextAreaIsVisible()
    {
        await Page.GotoAsync(BaseUrl);

        var input = Page.Locator("#messageInput");
        await Expect(input).ToBeVisibleAsync();
        await Expect(input).ToHaveAttributeAsync("placeholder", "Type a message... (Enter to send, Shift+Enter for new line)");
    }

    [Test]
    public async Task ChatInput_SendButtonIsVisible()
    {
        await Page.GotoAsync(BaseUrl);

        var sendBtn = Page.Locator("#sendBtn");
        await Expect(sendBtn).ToBeVisibleAsync();
    }

    [Test]
    public async Task ChatInput_AttachButtonIsVisible()
    {
        await Page.GotoAsync(BaseUrl);

        var attachBtn = Page.Locator("#attachBtn");
        await Expect(attachBtn).ToBeVisibleAsync();
    }

    [Test]
    public async Task ChatInput_CanTypeMessage()
    {
        await Page.GotoAsync(BaseUrl);

        var input = Page.Locator("#messageInput");
        await input.FillAsync("Hello, AI agent!");
        await Expect(input).ToHaveValueAsync("Hello, AI agent!");
    }

    [Test]
    public async Task ChatInput_FileInputIsHidden()
    {
        await Page.GotoAsync(BaseUrl);

        var fileInput = Page.Locator("#fileInput");
        await Expect(fileInput).ToBeHiddenAsync();
    }

    [Test]
    public async Task ChatInput_FilePreviewIsHiddenByDefault()
    {
        await Page.GotoAsync(BaseUrl);

        var preview = Page.Locator("#filePreview");
        await Expect(preview).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("hidden"));
    }
}
