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

        var endpoint = configuration["AZURE_FOUNDRY_PROJECT_ENDPOINT"];
        if (string.IsNullOrEmpty(endpoint))
        {
            _logger.LogWarning("AZURE_FOUNDRY_PROJECT_ENDPOINT not configured. Agent features will be disabled.");
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

            var deployment = _configuration["AZURE_MODEL_DEPLOYMENT_NAME"]
                ?? throw new InvalidOperationException("AZURE_MODEL_DEPLOYMENT_NAME is required");

            // Create Code Assistant (runtime-provisioned agent with GitHub MCP support)
            _agents["Code"] = await CreateAgentAsync(deployment, "github-agent", """
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

            // Register the Foundry-hosted Travel Agent (pre-deployed in Azure Foundry).
            var travelEndpoint = _configuration["AZURE_FOUNDRY_TRAVEL_AGENT_ENDPOINT"];
            if (!string.IsNullOrWhiteSpace(travelEndpoint))
            {
                _agentInfos["Travel"] = new ChatAgentInfo
                {
                    Name = "Travel",
                    Description = "Travel planning assistant (Foundry hosted)",
                    Avatar = "T",
                    Capabilities = ["Trip planning", "Destination info", "Travel tips", "Itinerary building"],
                    HasTools = true
                };
                _logger.LogInformation("Registered Foundry-hosted Travel agent from endpoint");
            }
            else
            {
                _logger.LogWarning("AZURE_FOUNDRY_TRAVEL_AGENT_ENDPOINT not configured. Travel agent will be unavailable.");
            }

            // Register the ContosoPay Customer Support Triage workflow agent (Foundry hosted).
            var triageAgentName = _configuration["AZURE_FOUNDRY_TRIAGE_AGENT_NAME"];
            if (!string.IsNullOrWhiteSpace(triageAgentName))
            {
                _agentInfos["Triage"] = new ChatAgentInfo
                {
                    Name = "Triage",
                    Description = "ContosoPay customer support triage (Foundry workflow)",
                    Avatar = "W",
                    Capabilities = ["Customer triage", "Support routing", "Issue classification", "ContosoPay workflows"],
                    HasTools = true
                };
                _logger.LogInformation("Registered Foundry workflow Triage agent: {AgentName}", triageAgentName);
            }
            else
            {
                _logger.LogWarning("AZURE_FOUNDRY_TRIAGE_AGENT_NAME not configured. Triage agent will be unavailable.");
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
            yield return new TextDeltaUpdate("Agent service is not configured. Please set AZURE_FOUNDRY_PROJECT_ENDPOINT in configuration.");
            yield break;
        }

        // --- Code agent: optionally attaches GitHub MCP tool per request when the user is authenticated ---
        if (agentName is "Code" or "GitHub")
        {
            var deployment = _configuration["AZURE_MODEL_DEPLOYMENT_NAME"]
                ?? throw new InvalidOperationException("AZURE_MODEL_DEPLOYMENT_NAME is required");
            var mcpUrl = _configuration["GITHUB_MCP_SERVER_URL"];

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
            var travelEndpoint = _configuration["AZURE_FOUNDRY_TRAVEL_AGENT_ENDPOINT"];
            if (string.IsNullOrWhiteSpace(travelEndpoint))
            {
                yield return new TextDeltaUpdate("Travel agent is not configured. Set AZURE_FOUNDRY_TRAVEL_AGENT_ENDPOINT.");
    yield break;
            }

// Extract agent name from the Foundry endpoint URL: .../applications/{name}/protocols/...
var travelAgentName = ExtractAgentName(travelEndpoint);
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

        // --- Triage workflow agent: uses the Foundry-hosted ContosoPay workflow agent ---
        // Workflow agents use the ActivityProtocol and do NOT support the "conversation"
        // field that the Responses API attaches when streaming or conversation state is
        // requested. Use non-streaming + StoredOutputEnabled=false to avoid that field.
        if (agentName == "Triage")
        {
            var triageAgentName = _configuration["AZURE_FOUNDRY_TRIAGE_AGENT_NAME"];
            if (string.IsNullOrWhiteSpace(triageAgentName))
            {
                yield return new TextDeltaUpdate("Triage agent is not configured. Set AZURE_FOUNDRY_TRIAGE_AGENT_NAME.");
                yield break;
            }

            var triageResponseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(triageAgentName);

            // Workflow agents are single-turn: include only the current user prompt.
            // Passing full history via InputItems causes "conversation" payload errors.
            var triageOptions = new CreateResponseOptions
            {
                StoredOutputEnabled = false
            };
            triageOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

            var triageResponse = await triageResponseClient.CreateResponseAsync(triageOptions, cancellationToken);
            var triageText = triageResponse.Value.GetOutputText();
            if (!string.IsNullOrEmpty(triageText))
                yield return new TextDeltaUpdate(triageText);
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
        var defaults = new List<ChatAgentInfo>
            {
                new ChatAgentInfo { Name = "Code", Description = "Coding + GitHub tools (MCP)", Avatar = "C", HasTools = true },
                new ChatAgentInfo { Name = "Travel", Description = "Travel planning assistant (Foundry hosted)", Avatar = "T", HasTools = true },
                new ChatAgentInfo { Name = "Triage", Description = "ContosoPay customer support triage (Foundry workflow)", Avatar = "W", HasTools = true }
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

/// <summary>
/// Extracts the agent name from a Foundry endpoint URL.
/// Expected format: .../applications/{agentName}/protocols/...
/// </summary>
private static string ExtractAgentName(string endpointUrl)
{
    var uri = new Uri(endpointUrl);
    var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < segments.Length - 1; i++)
    {
        if (segments[i].Equals("applications", StringComparison.OrdinalIgnoreCase))
            return segments[i + 1];
    }
    throw new InvalidOperationException(
        $"Cannot extract agent name from endpoint URL: {endpointUrl}. " +
        "Expected .../applications/{{agentName}}/protocols/...");
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
