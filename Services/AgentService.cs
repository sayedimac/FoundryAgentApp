#pragma warning disable OPENAI001

using System.Runtime.CompilerServices;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Core;
using Azure.Identity;
using OpenAI.Responses;
using WebApp.Models;

using ChatAgentInfo = WebApp.Models.AgentInfo;

namespace WebApp.Services;

public class AgentService : IAgentService, IAsyncDisposable
{
    private readonly AIProjectClient? _projectClient;
    private readonly Dictionary<string, AgentVersion> _agents = new();
    private readonly Dictionary<string, ChatAgentInfo> _agentInfos = new();
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentService> _logger;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public AgentService(
        IConfiguration configuration,
        ILogger<AgentService> logger,
        IWebHostEnvironment environment,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _logger = logger;

        var endpoint = GetStringConfig(configuration, "AZURE_PROJECT_ENDPOINT", "Azure:ProjectEndpoint");
        if (string.IsNullOrEmpty(endpoint))
        {
            _logger.LogWarning("Azure:ProjectEndpoint not configured. Agent features will be disabled.");
            return;
        }

        TokenCredential credential;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
        {
            // Running in Azure App Service – use Managed Identity.
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            credential = string.IsNullOrWhiteSpace(clientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(clientId);

            _logger.LogInformation("Using ManagedIdentityCredential (clientId={ClientId})",
                string.IsNullOrWhiteSpace(clientId) ? "system" : clientId);
        }
        else
        {
            credential = new ChainedTokenCredential(
                new VisualStudioCredential(),
                new AzureCliCredential(),
                new EnvironmentCredential());
        }

        _projectClient = new AIProjectClient(new Uri(endpoint), credential);
        _logger.LogInformation("AIProjectClient created for endpoint {Endpoint}", endpoint);
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _projectClient == null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            var deployment = GetStringConfig(_configuration, "AZURE_MODEL_DEPLOYMENT_NAME", "Azure:ModelDeploymentName")
                ?? throw new InvalidOperationException("AZURE_MODEL_DEPLOYMENT_NAME (or Azure:ModelDeploymentName) is required");

            // Create Code Assistant (runtime-provisioned agent with GitHub MCP support)
            _agents["Code"] = await CreateAgentAsync(deployment, "ChatCode", """
                You are a helpful code assistant.
                You help with programming questions, code review, debugging, and explaining code.
                Format responses in Markdown.Use fenced code blocks with language identifiers.
                If GitHub MCP tools are available, you may use them to search code, issues, and PRs.
                Be concise but thorough.
                """);
            _agentInfos["Code"] = new ChatAgentInfo
            {
                Name = "Code",
                Description = "Coding + GitHub tools (MCP)",
                Avatar = "C",
                Capabilities = ["Code review", "Debugging", "Explanations", "Best practices", "GitHub MCP tools"],
                HasTools = true
            };

            // Register the Foundry-hosted Travel Agent (already exists in the Foundry project).
            var travelAgentName = GetStringConfig(_configuration, "AZURE_TRAVEL_AGENT_NAME", "Azure:TravelAgentName");
            if (!string.IsNullOrWhiteSpace(travelAgentName))
            {
                _agentInfos["Travel"] = new ChatAgentInfo
                {
                    Name = "Travel",
                    Description = "Travel planning assistant (Foundry hosted)",
                    Avatar = "T",
                    Capabilities = ["Trip planning", "Destination info", "Travel tips"],
                    HasTools = true
                };
                _logger.LogInformation("Registered Foundry-hosted Travel agent: {Name}", travelAgentName);
            }
            else
            {
                _logger.LogWarning("AZURE_TRAVEL_AGENT_NAME not configured. Travel agent will be unavailable.");
            }

            _initialized = true;
            _logger.LogInformation("Initialized agents: {Agents}", string.Join(", ", _agentInfos.Keys));
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async IAsyncEnumerable<AgentStreamUpdate> GetStreamingResponseAsync(
        string agentName,
        string prompt,
        IEnumerable<ChatMessage> history,
        IReadOnlyList<string>? imagePaths = null,
        string? githubToken = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        if (_projectClient == null)
        {
            yield return new TextDeltaUpdate("Agent service is not configured. Please set Azure:ProjectEndpoint in configuration.");
            yield break;
        }

        // --- Code agent: optionally attaches GitHub MCP tool per request when the user is authenticated ---
        if (agentName is "Code" or "GitHub")
        {
            var deployment = GetStringConfig(_configuration, "AZURE_MODEL_DEPLOYMENT_NAME", "Azure:ModelDeploymentName")
                ?? throw new InvalidOperationException("AZURE_MODEL_DEPLOYMENT_NAME (or Azure:ModelDeploymentName) is required");
            var mcpUrl = GetStringConfig(_configuration, "GITHUB_MCP_SERVER_URL", "Azure:GitHubMcpServerUrl");

            var requiresAuth = agentName == "GitHub";
            if (requiresAuth && string.IsNullOrEmpty(githubToken))
            {
                yield return new TextDeltaUpdate("Please sign in with GitHub first to use GitHub MCP tools.");
                yield break;
            }

            // Only attach MCP tools when we have a token + MCP server URL.
            if (!string.IsNullOrEmpty(githubToken) && !string.IsNullOrEmpty(mcpUrl))
            {
                var responsesClient = _projectClient.OpenAI.GetProjectResponsesClientForModel(deployment);

                var mcpOptions = new CreateResponseOptions
                {
                    Instructions = """
                        You are a helpful software engineering assistant.
                        Format responses in Markdown and use fenced code blocks with language identifiers.
                        You have access to GitHub via MCP when needed; prefer using tools for repo / issue / code facts.
                        Be concise but thorough.
                        """
                };

            foreach (var msg in history.TakeLast(20))
            {
                mcpOptions.InputItems.Add(msg.Role == "user"
                    ? ResponseItem.CreateUserMessageItem(msg.Content)
                    : ResponseItem.CreateAssistantMessageItem(msg.Content));
            }
            mcpOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

            var mcpTool = ResponseTool.CreateMcpTool(
                serverLabel: "github",
                serverUri: new Uri(mcpUrl),
                authorizationToken: githubToken,
                toolCallApprovalPolicy: new McpToolCallApprovalPolicy(
                    GlobalMcpToolCallApprovalPolicy.AlwaysRequireApproval));
            mcpOptions.Tools.Add(mcpTool);

            await foreach (var update in HandleMcpStreamingAsync(responsesClient, mcpOptions, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        // No MCP available; fall through to normal agent streaming below.
    }

        // --- Travel agent: uses the Foundry-hosted agent by name (not a runtime-created version) ---
        if (agentName == "Travel")
        {
            var travelAgentName = GetStringConfig(_configuration, "AZURE_TRAVEL_AGENT_NAME", "Azure:TravelAgentName");
            if (string.IsNullOrWhiteSpace(travelAgentName))
            {
                yield return new TextDeltaUpdate("Travel agent is not configured. Set AZURE_TRAVEL_AGENT_NAME (or Azure:TravelAgentName) to the agent name in your Foundry project.");
    yield break;
            }

var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(travelAgentName);

var options = new CreateResponseOptions
{
    StreamingEnabled = true
};
            foreach (var msg in history.TakeLast(20))
            {
                options.InputItems.Add(msg.Role == "user"
                    ? ResponseItem.CreateUserMessageItem(msg.Content)
                    : ResponseItem.CreateAssistantMessageItem(msg.Content));
            }
            options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

await foreach (var update in responseClient.CreateResponseStreamingAsync(options, cancellationToken))
{
    if (update is StreamingResponseOutputTextDeltaUpdate textDelta)
    {
        yield return new TextDeltaUpdate(textDelta.Delta);
    }
}
yield break;
        }

        // --- Runtime-created agents (Code without MCP falls through here) ---
        if (!_agents.TryGetValue(agentName, out var agent))
{
    yield return new TextDeltaUpdate($"Unknown agent: {agentName}. Available agents: {string.Join(", ", _agentInfos.Keys)}");
    yield break;
}

var agentResponseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(agent.Name);

var agentOptions = new CreateResponseOptions
{
    StreamingEnabled = true
};
foreach (var msg in history.TakeLast(20))
{
    agentOptions.InputItems.Add(msg.Role == "user"
        ? ResponseItem.CreateUserMessageItem(msg.Content)
        : ResponseItem.CreateAssistantMessageItem(msg.Content));
}
agentOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

await foreach (var update in agentResponseClient.CreateResponseStreamingAsync(agentOptions, cancellationToken))
{
    if (update is StreamingResponseOutputTextDeltaUpdate textDelta)
    {
        yield return new TextDeltaUpdate(textDelta.Delta);
    }
}
    }

    public Task<ChatAgentInfo> GetAgentInfoAsync(string agentName)
{
    if (_agentInfos.TryGetValue(agentName, out var info))
    {
        return Task.FromResult(info);
    }

    return Task.FromResult(new ChatAgentInfo
    {
        Name = agentName,
        Description = "Unknown agent",
        Avatar = agentName[0].ToString().ToUpper()
    });
}

public Task<IEnumerable<ChatAgentInfo>> GetAvailableAgentsAsync()
{
    if (_agentInfos.Count == 0)
    {
        // Return default list if not initialized
        var defaults = new List<ChatAgentInfo>
            {
                new ChatAgentInfo { Name = "Code", Description = "Coding + GitHub tools (MCP)", Avatar = "C", HasTools = true },
                new ChatAgentInfo { Name = "Travel", Description = "Travel planning assistant (Foundry hosted)", Avatar = "T", HasTools = true }
            };

        return Task.FromResult<IEnumerable<ChatAgentInfo>>(defaults);
    }

    return Task.FromResult<IEnumerable<ChatAgentInfo>>(_agentInfos.Values);
}

private async Task<AgentVersion> CreateAgentAsync(string model, string name, string instructions)
{
    var definition = new PromptAgentDefinition(model: model)
    {
        Instructions = instructions
    };

    var result = await _projectClient!.Agents.CreateAgentVersionAsync(
        agentName: name,
        options: new AgentVersionCreationOptions(definition));

    _logger.LogInformation("Created agent: {Name} v{Version}", result.Value.Name, result.Value.Version);
    return result.Value;
}

private async IAsyncEnumerable<AgentStreamUpdate> HandleMcpStreamingAsync(
    ProjectResponsesClient responseClient,
    CreateResponseOptions options,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    ResponseResult? latestResponse = null;
    CreateResponseOptions? nextOptions = options;

    while (nextOptions != null)
    {
        latestResponse = await responseClient.CreateResponseAsync(nextOptions, cancellationToken);
        nextOptions = null;

        var approvals = latestResponse.OutputItems
            .OfType<McpToolCallApprovalRequestItem>()
            .ToList();

        if (approvals.Count > 0)
        {
            foreach (var approval in approvals)
            {
                yield return new ToolCallUpdate(approval.ServerLabel, "Requesting approval...");
            }

            // Auto-approve for demo purposes.
            nextOptions = new CreateResponseOptions
            {
                PreviousResponseId = latestResponse.Id,
                Instructions = options.Instructions,
                StreamingEnabled = options.StreamingEnabled
            };
            foreach (var tool in options.Tools)
            {
                nextOptions.Tools.Add(tool);
            }

            foreach (var approval in approvals)
            {
                nextOptions.InputItems.Add(ResponseItem.CreateMcpApprovalResponseItem(
                    approvalRequestId: approval.Id,
                    approved: true));

                yield return new ToolResultUpdate(approval.ServerLabel, "Approved");
            }
        }
    }

    if (latestResponse != null)
    {
        var outputText = latestResponse.GetOutputText();
        if (!string.IsNullOrEmpty(outputText))
        {
            yield return new TextDeltaUpdate(outputText);
        }
    }
}

private static string? GetStringConfig(IConfiguration configuration, string flatKey, string legacyKey)
    => configuration[flatKey] ?? configuration[legacyKey];

public async ValueTask DisposeAsync()
{
    if (_projectClient == null) return;

    // Only delete runtime-created agents (Code). The Travel agent is hosted and not owned by this app.
    foreach (var (name, agent) in _agents)
    {
        try
        {
            await _projectClient.Agents.DeleteAgentVersionAsync(agent.Name, agent.Version);
            _logger.LogInformation("Deleted agent: {Name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup agent {Name}", name);
        }
    }

    _agents.Clear();
    _agentInfos.Clear();
    GC.SuppressFinalize(this);
}
}
