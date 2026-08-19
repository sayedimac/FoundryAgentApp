#pragma warning disable OPENAI001

using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Core;
using Azure.Identity;
using OpenAI.Responses;
using WebApp.Models;

using ChatAgentInfo = WebApp.Models.AgentInfo;

namespace WebApp.Services;

/// <summary>
/// Coordinates chat requests against Azure AI Foundry agents.
///
/// Three kinds of agents are supported:
///  - Self-registering agents (e.g. "Code", "Weather"): created on first use and cached for the
///    lifetime of the process. They don't need to exist in the Foundry project beforehand.
///  - Foundry-hosted agents: discovered by listing the agents already deployed to the configured
///    Foundry project, so they show up automatically in the chat dropdown.
///  - Workflow agents (e.g. "Human"): call a named, pre-deployed Foundry workflow (activity
///    protocol) rather than the standard Responses API.
/// </summary>
public class AgentService : IAgentService, IAsyncDisposable
{
    // Workflow agents are single-turn Foundry-hosted agents addressed by name via configuration.
    private static readonly IReadOnlyDictionary<string, (string ConfigKey, string Description, string Avatar)> WorkflowAgents =
        new Dictionary<string, (string, string, string)>
        {
            ["Triage"] = ("AZURE_FOUNDRY_TRIAGE_AGENT_NAME", "Customer support triage (Foundry workflow)", "T"),
            ["Human"] = ("AZURE_FOUNDRY_HUMAN_AGENT_NAME", "Human-in-the-loop approval workflow (Foundry workflow)", "H"),
        };

    private readonly AIProjectClient? _projectClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly Dictionary<string, AgentVersion> _selfRegisteredAgents = new();
    private readonly Dictionary<string, ChatAgentInfo> _agentInfos = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _registrationLock = new(1, 1);
    private bool _initialized;

    public AgentService(
        IConfiguration configuration,
        ILogger<AgentService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var endpoint = configuration["AZURE_FOUNDRY_PROJECT_ENDPOINT"];
        if (string.IsNullOrEmpty(endpoint))
        {
            _logger.LogWarning("AZURE_FOUNDRY_PROJECT_ENDPOINT not configured. Agent features will be disabled.");
            return;
        }

        _projectClient = new AIProjectClient(new Uri(endpoint), CreateCredential());
        _logger.LogInformation("AIProjectClient created for endpoint {Endpoint}", endpoint);
    }

    private static TokenCredential CreateCredential()
    {
        // Running in Azure App Service – use Managed Identity.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")))
        {
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            return string.IsNullOrWhiteSpace(clientId)
                ? new ManagedIdentityCredential()
                : new ManagedIdentityCredential(clientId);
        }

        return new ChainedTokenCredential(
            new VisualStudioCredential(),
            new AzureCliCredential(),
            new EnvironmentCredential());
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _projectClient == null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            // Self-registering agents show up in the dropdown immediately; they are only
            // created in the Foundry project the first time they're actually used.
            _agentInfos["Code"] = new ChatAgentInfo
            {
                Name = "Code",
                Description = "Coding + GitHub tools (MCP)",
                Avatar = "C",
                Capabilities = ["Code review", "Debugging", "Explanations", "Best practices", "GitHub MCP tools"],
                HasTools = true
            };

            _agentInfos["Weather"] = new ChatAgentInfo
            {
                Name = "Weather",
                Description = "Weather forecasts via Azure Function (OpenWeatherMap)",
                Avatar = "W",
                Capabilities = ["Current conditions", "Temperature", "Forecasts"],
                HasTools = true
            };

            RegisterTravelAgent();
            RegisterWorkflowAgents();
            await DiscoverFoundryAgentsAsync();

            _initialized = true;
            _logger.LogInformation("Initialized agents: {Agents}", string.Join(", ", _agentInfos.Keys));
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void RegisterTravelAgent()
    {
        var travelEndpoint = _configuration["AZURE_FOUNDRY_TRAVEL_AGENT_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(travelEndpoint))
        {
            _logger.LogInformation("AZURE_FOUNDRY_TRAVEL_AGENT_ENDPOINT not configured. Travel agent will be unavailable.");
            return;
        }

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

    private void RegisterWorkflowAgents()
    {
        foreach (var (name, (configKey, description, avatar)) in WorkflowAgents)
        {
            var agentName = _configuration[configKey];
            if (string.IsNullOrWhiteSpace(agentName))
            {
                _logger.LogInformation("{ConfigKey} not configured. {Name} agent will be unavailable.", configKey, name);
                continue;
            }

            _agentInfos[name] = new ChatAgentInfo
            {
                Name = name,
                Description = description,
                Avatar = avatar,
                Capabilities = ["Foundry workflow"],
                HasTools = true
            };
            _logger.LogInformation("Registered Foundry workflow agent {Name}: {AgentName}", name, agentName);
        }
    }

    /// <summary>
    /// Enumerates the agents already deployed on the configured Foundry project so they
    /// automatically show up in the chat dropdown without any code changes.
    /// </summary>
    private async Task DiscoverFoundryAgentsAsync()
    {
        if (_projectClient == null) return;

        try
        {
            await foreach (var agent in _projectClient.Agents.GetAgentsAsync())
            {
                if (string.IsNullOrWhiteSpace(agent.Name) || _agentInfos.ContainsKey(agent.Name))
                {
                    continue;
                }

                _agentInfos[agent.Name] = new ChatAgentInfo
                {
                    Name = agent.Name,
                    Description = "Foundry hosted agent",
                    Avatar = agent.Name[0].ToString().ToUpperInvariant(),
                    Capabilities = ["Foundry hosted"],
                    HasTools = true
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate agents on the Foundry project.");
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

        if (agentName == "Weather")
        {
            var weatherUpdate = await GetWeatherResponseAsync(prompt, cancellationToken);
            yield return weatherUpdate;
            yield break;
        }

        if (agentName == "Code" || agentName == "GitHub")
        {
            await foreach (var update in HandleCodeAgentAsync(agentName, prompt, history, githubToken, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        if (agentName == "Travel")
        {
            await foreach (var update in HandleFoundryHostedAgentAsync(ExtractAgentName(_configuration["AZURE_FOUNDRY_TRAVEL_AGENT_ENDPOINT"]!), prompt, history, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        if (WorkflowAgents.TryGetValue(agentName, out var workflow))
        {
            var configuredName = _configuration[workflow.ConfigKey];
            if (string.IsNullOrWhiteSpace(configuredName))
            {
                yield return new TextDeltaUpdate($"{agentName} agent is not configured. Set {workflow.ConfigKey}.");
                yield break;
            }

            await foreach (var update in HandleWorkflowAgentAsync(configuredName, prompt, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        // Any other agent is assumed to be hosted on the Foundry project (discovered via
        // DiscoverFoundryAgentsAsync) and addressed directly by name.
        await foreach (var update in HandleFoundryHostedAgentAsync(agentName, prompt, history, cancellationToken))
        {
            yield return update;
        }
    }

    // --- Code agent: self-registers on first use; optionally attaches GitHub MCP tools ---
    private async IAsyncEnumerable<AgentStreamUpdate> HandleCodeAgentAsync(
        string agentName,
        string prompt,
        IEnumerable<ChatMessage> history,
        string? githubToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var requiresAuth = agentName == "GitHub";
        if (requiresAuth && string.IsNullOrEmpty(githubToken))
        {
            yield return new TextDeltaUpdate("Please sign in with GitHub first to use GitHub MCP tools.");
            yield break;
        }

        var deployment = _configuration["AZURE_MODEL_DEPLOYMENT_NAME"]
            ?? throw new InvalidOperationException("AZURE_MODEL_DEPLOYMENT_NAME is required");
        var mcpUrl = _configuration["GITHUB_MCP_SERVER_URL"];

        // Only attach MCP tools when we have a token + MCP server URL; otherwise fall through
        // to the plain self-registered Code agent.
        if (!string.IsNullOrEmpty(githubToken) && !string.IsNullOrEmpty(mcpUrl))
        {
            var responsesClient = _projectClient!.OpenAI.GetProjectResponsesClientForModel(deployment);

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

        var agent = await GetOrCreateSelfRegisteredAgentAsync("Code", deployment, """
            You are a helpful code assistant.
            You help with programming questions, code review, debugging, and explaining code.
            Format responses in Markdown. Use fenced code blocks with language identifiers.
            Be concise but thorough.
            """);

        await foreach (var update in HandleFoundryHostedAgentAsync(agent.Name, prompt, history, cancellationToken))
        {
            yield return update;
        }
    }

    // --- Weather agent: self-registers on first use and calls an Azure Function that talks to OpenWeatherMap ---
    private async Task<AgentStreamUpdate> GetWeatherResponseAsync(string prompt, CancellationToken cancellationToken)
    {
        var functionUrl = _configuration["WEATHER_FUNCTION_APP_URL"];
        if (string.IsNullOrWhiteSpace(functionUrl))
        {
            return new TextDeltaUpdate("Weather agent is not configured. Set WEATHER_FUNCTION_APP_URL (and WEATHER_FUNCTION_APP_KEY if required).");
        }

        var location = ExtractLocation(prompt);
        if (string.IsNullOrWhiteSpace(location))
        {
            return new TextDeltaUpdate("Which city would you like the weather for?");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var uriBuilder = new UriBuilder(functionUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
            query["location"] = location;

            var functionKey = _configuration["WEATHER_FUNCTION_APP_KEY"];
            if (!string.IsNullOrWhiteSpace(functionKey))
            {
                query["code"] = functionKey;
            }

            uriBuilder.Query = query.ToString();

            using var response = await client.GetAsync(uriBuilder.Uri, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Weather function call failed with status {Status}: {Body}", response.StatusCode, body);
                return new TextDeltaUpdate($"Sorry, I couldn't get the weather for {location} right now.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
            var temperature = root.TryGetProperty("temperature", out var t) ? t.GetDouble().ToString("0.#") : null;
            var resolvedLocation = root.TryGetProperty("location", out var l) ? l.GetString() : location;

            var message = string.IsNullOrWhiteSpace(temperature)
                ? $"It is {description ?? "unclear"} today in {resolvedLocation}."
                : $"It is {description ?? "unclear"} today in {resolvedLocation}, with a temperature of {temperature}\u00b0C.";

            return new TextDeltaUpdate(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to call weather function for location {Location}", location);
            return new TextDeltaUpdate($"Sorry, I couldn't get the weather for {location} right now.");
        }
    }

    /// <summary>Very small heuristic to pull a location out of a free-form weather question.</summary>
    private static string ExtractLocation(string prompt)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            prompt, @"\bin\s+([A-Za-z\s,]+?)\s*[\?\.!]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : prompt.Trim();
    }

    // --- Foundry-hosted agents: addressed directly by their Foundry agent name ---
    private async IAsyncEnumerable<AgentStreamUpdate> HandleFoundryHostedAgentAsync(
        string foundryAgentName,
        string prompt,
        IEnumerable<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responseClient = _projectClient!.OpenAI.GetProjectResponsesClientForAgent(foundryAgentName);

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
    }

    // --- Workflow agents: use the ActivityProtocol and are single-turn (no conversation state) ---
    private async IAsyncEnumerable<AgentStreamUpdate> HandleWorkflowAgentAsync(
        string foundryAgentName,
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responseClient = _projectClient!.OpenAI.GetProjectResponsesClientForAgent(foundryAgentName);

        // Workflow agents don't support the "conversation" field that the Responses API attaches
        // when streaming or conversation state is requested, so only send the current prompt.
        var options = new CreateResponseOptions
        {
            StoredOutputEnabled = false
        };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(prompt));

        var response = await responseClient.CreateResponseAsync(options, cancellationToken);
        var text = response.Value.GetOutputText();
        if (!string.IsNullOrEmpty(text))
        {
            yield return new TextDeltaUpdate(text);
        }
    }

    private async Task<AgentVersion> GetOrCreateSelfRegisteredAgentAsync(string key, string model, string instructions)
    {
        if (_selfRegisteredAgents.TryGetValue(key, out var existing))
        {
            return existing;
        }

        await _registrationLock.WaitAsync();
        try
        {
            if (_selfRegisteredAgents.TryGetValue(key, out existing))
            {
                return existing;
            }

            var definition = new PromptAgentDefinition(model: model)
            {
                Instructions = instructions
            };

            var result = await _projectClient!.Agents.CreateAgentVersionAsync(
                agentName: key.ToLowerInvariant(),
                options: new AgentVersionCreationOptions(definition));

            _logger.LogInformation("Self-registered agent: {Name} v{Version}", result.Value.Name, result.Value.Version);
            _selfRegisteredAgents[key] = result.Value;
            return result.Value;
        }
        finally
        {
            _registrationLock.Release();
        }
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
            Avatar = agentName[0].ToString().ToUpperInvariant()
        });
    }

    public async Task<IEnumerable<ChatAgentInfo>> GetAvailableAgentsAsync()
    {
        await InitializeAsync();

        if (_agentInfos.Count == 0)
        {
            // Agent service isn't configured (no Foundry endpoint); still show the
            // self-registering agents so the UI has something to display.
            return new List<ChatAgentInfo>
            {
                new() { Name = "Code", Description = "Coding + GitHub tools (MCP)", Avatar = "C", HasTools = true },
                new() { Name = "Weather", Description = "Weather forecasts via Azure Function (OpenWeatherMap)", Avatar = "W", HasTools = true }
            };
        }

        return _agentInfos.Values;
    }

    public async ValueTask DisposeAsync()
    {
        if (_projectClient == null) return;

        // Only delete self-registered agents (e.g. Code, Weather). Foundry-hosted agents
        // (Travel, workflow agents, discovered agents) are not owned by this app.
        foreach (var (name, agent) in _selfRegisteredAgents)
        {
            try
            {
                await _projectClient.Agents.DeleteAgentVersionAsync(agent.Name, agent.Version);
                _logger.LogInformation("Deleted self-registered agent: {Name}", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup agent {Name}", name);
            }
        }

        _selfRegisteredAgents.Clear();
        _agentInfos.Clear();
        GC.SuppressFinalize(this);
    }
}
