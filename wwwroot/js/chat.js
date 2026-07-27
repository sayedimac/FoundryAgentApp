// GitHub-styled Chat Application
// SignalR client for real-time AI chat

class ChatApp {
    constructor() {
        this.connection = null;
        this.sessionId = null;
        this.currentAgent = 'Code';
        this.attachedFiles = [];
        this.currentMessageId = null;
        this.currentMessageContent = '';
        this.isConnected = false;
        this.reconnectAttempts = 0;
        this.maxReconnectAttempts = 5;

        this.githubUsername = null;
        this.gitHubMcpBusyCount = 0;
        this.agents = [];

        // Per-agent suggested prompts (UI-only, not from server config)
        this.suggestedPrompts = {
            Code: [
                'Search for open issues in my repository',
                'Review this code snippet for potential bugs',
                'Explain how async/await works in C#',
                'Find recent pull requests in my project'
            ],
            Travel: [
                'Plan a 7-day trip to Tokyo, Japan',
                'What are the best beaches in Thailand?',
                'Create a weekend getaway itinerary for Paris',
                'What should I pack for a trip to Iceland in winter?'
            ]
        };

        // Single Page mode state
        this.uiMode = 'chat';           // 'chat' | 'singlepage'
        this.spHistory = [];             // [{prompt, content, title}]
        this.spCurrentIndex = -1;
        this.spSelectedText = '';
        this.spCurrentPrompt = '';

        this.init();
    }

    async init() {
        this.applyDefaultTheme();
        await this.setupSignalR();
        this.setupEventListeners();
        this.setupMarkdown();
        this.handleOAuthReturn();
    }

    handleOAuthReturn() {
        const params = new URLSearchParams(window.location.search);
        if (params.get('github_auth') === 'success') {
            const agent = params.get('agent') || 'Code';
            const username = params.get('username');

            // Pre-select the Code agent (which hosts GitHub MCP tools) in the dropdown
            const select = document.getElementById('agentSelect');
            if (select) {
                select.value = agent;
            }
            this.selectAgent(agent);

            if (username) {
                this.githubUsername = username;
                this.setGitHubMcpStatus('connected', { username });
                this.addSystemMessage(`Connected to GitHub as ${username}`, 'info');
            }

            // Clean the URL so the query params don't linger
            window.history.replaceState({}, '', '/');
        }
    }

    applyDefaultTheme() {
        const container = document.getElementById('chatContainer');
        if (container) {
            container.dataset.theme = 'dark';
        }
    }

    async setupSignalR() {
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl('/chathub')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Information)
            .build();

        // Connection state handlers
        this.connection.onreconnecting(error => {
            console.log('Reconnecting...', error);
            this.showConnectionStatus('Reconnecting...');
        });

        this.connection.onreconnected(connectionId => {
            console.log('Reconnected:', connectionId);
            this.hideConnectionStatus();
            this.isConnected = true;
        });

        this.connection.onclose(error => {
            console.log('Connection closed:', error);
            this.isConnected = false;
            this.showConnectionStatus('Disconnected. Refresh to reconnect.');
        });

        // Message handlers
        this.connection.on('ReceiveMessageChunk', (messageId, chunk) => {
            this.handleMessageChunk(messageId, chunk);
        });

        this.connection.on('MessageComplete', (messageId) => {
            this.handleMessageComplete(messageId);
        });

        this.connection.on('ReceiveToolCall', (messageId, toolName, args) => {
            this.showToolNotification(toolName, 'Running...');

            if (this.currentAgent === 'Code') {
                this.gitHubMcpBusyCount++;
                this.setGitHubMcpStatus('busy');
            }
        });

        this.connection.on('ReceiveToolResult', (messageId, toolName, result) => {
            this.showToolNotification(toolName, 'Complete');
            setTimeout(() => this.hideToolNotification(), 2000);

            if (this.currentAgent === 'Code') {
                this.gitHubMcpBusyCount = Math.max(0, this.gitHubMcpBusyCount - 1);
                if (this.gitHubMcpBusyCount === 0) {
                    this.setGitHubMcpStatus(this.githubUsername ? 'connected' : 'disconnected', {
                        username: this.githubUsername
                    });
                }
            }
        });

        this.connection.on('ReceiveError', (messageId, error) => {
            this.hideTypingIndicator();
            this.addSystemMessage(`Error: ${error}`, 'error');
        });

        this.connection.on('AgentSelected', (agentInfo) => {
            this.updateAgentDisplay(agentInfo);
        });

        this.connection.on('SessionCreated', (sessionId) => {
            this.sessionId = sessionId;
            console.log('Session created:', sessionId);
        });

        this.connection.on('SessionCleared', (sessionId) => {
            this.clearMessageList();
            this.showWelcomeMessage();
        });

        try {
            await this.connection.start();
            console.log('SignalR connected');
            this.isConnected = true;
            await this.createSession();
            await this.loadAgents();
        } catch (error) {
            console.error('SignalR connection failed:', error);
            this.showConnectionStatus('Connection failed. Check console for details.');
        }
    }

    setupEventListeners() {
        // Form submission
        document.getElementById('chatForm').addEventListener('submit', (e) => {
            e.preventDefault();
            this.sendMessage();
        });

        // Agent selection
        document.getElementById('agentSelect').addEventListener('change', (e) => {
            this.selectAgent(e.target.value);
        });

        // UI mode selection
        const uiModeSelect = document.getElementById('uiModeSelect');
        if (uiModeSelect) {
            uiModeSelect.addEventListener('change', (e) => this.switchUiMode(e.target.value));
        }

        // New chat button
        document.getElementById('newChatBtn').addEventListener('click', () => {
            this.createNewSession();
        });

        // Clear chat button
        document.getElementById('clearChatBtn').addEventListener('click', () => {
            this.clearSession();
        });

        // File attachment
        document.getElementById('attachBtn').addEventListener('click', () => {
            document.getElementById('fileInput').click();
        });

        document.getElementById('fileInput').addEventListener('change', (e) => {
            this.handleFileSelect(e.target.files);
        });

        // Auto-resize textarea
        const textarea = document.getElementById('messageInput');
        textarea.addEventListener('input', () => {
            textarea.style.height = 'auto';
            textarea.style.height = Math.min(textarea.scrollHeight, 200) + 'px';
        });

        // Enter to send, Shift+Enter for new line
        textarea.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                this.sendMessage();
            }
        });

        // Single Page navigation buttons
        document.getElementById('spBackBtn').addEventListener('click', () => this.navigateBack());
        document.getElementById('spForwardBtn').addEventListener('click', () => this.navigateForward());

        // Single Page context menu actions
        document.getElementById('spPromptOnSelected').addEventListener('click', () => this.promptOnSelected());
        document.getElementById('spMoreOnThis').addEventListener('click', () => this.moreOnThis());

        // Single Page inline prompt
        document.getElementById('spInlineSubmit').addEventListener('click', () => this.submitInlinePrompt());
        document.getElementById('spInlineCancel').addEventListener('click', () => this.hideInlinePrompt());
        document.getElementById('spInlineInput').addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                this.submitInlinePrompt();
            }
        });

        // Text selection in SP content (show context menu)
        const spContent = document.getElementById('spContent');
        if (spContent) {
            spContent.addEventListener('mouseup', (e) => this.handleTextSelection(e));
        }
        document.addEventListener('keyup', (e) => {
            if (this.uiMode === 'singlepage') this.handleTextSelection(e);
        });

        // Hide context menu when clicking outside it or the inline prompt
        document.addEventListener('mousedown', (e) => {
            if (!e.target.closest('#spContextMenu') && !e.target.closest('#spInlinePrompt')) {
                this.hideSpContextMenu();
            }
        });
    }

    setupMarkdown() {
        marked.setOptions({
            breaks: true,
            gfm: true,
            highlight: (code, lang) => {
                if (lang && hljs.getLanguage(lang)) {
                    try {
                        return hljs.highlight(code, { language: lang }).value;
                    } catch (e) {
                        console.error('Highlight error:', e);
                    }
                }
                return hljs.highlightAuto(code).value;
            }
        });
    }

    async sendMessage() {
        const input = document.getElementById('messageInput');
        const message = input.value.trim();

        if (!message || !this.isConnected) return;

        // Clear input and reset height
        input.value = '';
        input.style.height = 'auto';

        if (this.uiMode === 'singlepage') {
            this.spCurrentPrompt = message;

            // Show loading state in SP content
            const contentEl = document.getElementById('spContent');
            if (contentEl) {
                contentEl.innerHTML = '<div class="sp-loading"><span class="sp-loading-text">Generating response…</span></div>';
            }

            // If not at the latest item, discard forward history
            if (this.spCurrentIndex < this.spHistory.length - 1) {
                this.spHistory = this.spHistory.slice(0, this.spCurrentIndex + 1);
            }

            this.showTypingIndicator();

            try {
                await this.connection.invoke('SendMessage', this.sessionId, this.currentAgent, message, []);
            } catch (error) {
                console.error('Send message error (SP):', error);
                this.hideTypingIndicator();
                const contentElErr = document.getElementById('spContent');
                if (contentElErr) {
                    contentElErr.innerHTML = `<p style="color:var(--error-color)">Failed to send: ${this.escapeHtml(error.message)}</p>`;
                }
            }
            return;
        }

        // Snapshot attachments before we clear them.
        const outgoingAttachments = Array.isArray(this.attachedFiles) ? [...this.attachedFiles] : [];

        // Hide welcome message if visible
        this.hideWelcomeMessage();

        // Add user message to UI
        this.addMessageToUI('user', message, outgoingAttachments);

        // Show typing indicator
        this.showTypingIndicator();

        try {
            const imagePaths = (this.attachedFiles || [])
                .filter(a => (a.contentType || '').toLowerCase().startsWith('image/') && a.storagePath)
                .map(a => a.storagePath);

            await this.connection.invoke('SendMessage', this.sessionId, this.currentAgent, message, imagePaths);

            // Clear attachments after send
            this.attachedFiles = [];
            const filePreview = document.getElementById('filePreview');
            if (filePreview) {
                filePreview.classList.add('hidden');
                const previewList = filePreview.querySelector('.file-preview-list');
                if (previewList) previewList.innerHTML = '';
            }
        } catch (error) {
            console.error('Send message error:', error);
            this.hideTypingIndicator();
            this.addSystemMessage(`Failed to send message: ${error.message}`, 'error');
        }
    }

    handleMessageChunk(messageId, chunk) {
        this.hideTypingIndicator();

        if (this.uiMode === 'singlepage') {
            if (this.currentMessageId !== messageId) {
                this.currentMessageId = messageId;
                this.currentMessageContent = '';
            }
            this.currentMessageContent += chunk;

            const contentEl = document.getElementById('spContent');
            if (contentEl) {
                // Show plain text while streaming for performance
                contentEl.textContent = this.currentMessageContent;
                contentEl.scrollTop = contentEl.scrollHeight;
            }
            return;
        }

        if (this.currentMessageId !== messageId) {
            // New message from assistant
            this.currentMessageId = messageId;
            this.currentMessageContent = '';
            this.createAssistantMessage(messageId);
        }

        // Append chunk
        this.currentMessageContent += chunk;
        this.updateMessageContent(messageId, this.currentMessageContent);
    }

    handleMessageComplete(messageId) {
        this.hideTypingIndicator();

        if (this.uiMode === 'singlepage') {
            const contentEl = document.getElementById('spContent');
            if (contentEl) {
                contentEl.innerHTML = marked.parse(this.currentMessageContent);
                contentEl.querySelectorAll('pre code').forEach((block) => {
                    hljs.highlightElement(block);
                });
                contentEl.scrollTop = 0;
            }

            // Add this page to history
            const title = this.spCurrentPrompt.length > 40
                ? this.spCurrentPrompt.substring(0, 40) + '…'
                : this.spCurrentPrompt;
            this.addToSpHistory(this.spCurrentPrompt, this.currentMessageContent, title);
            this.updateSpBreadcrumb();

            this.currentMessageId = null;
            this.currentMessageContent = '';
            return;
        }

        // Final render with full markdown
        const contentEl = document.querySelector(`[data-message-id="${messageId}"] .message-body`);
        if (contentEl) {
            contentEl.innerHTML = marked.parse(this.currentMessageContent);
            // Re-highlight code blocks
            contentEl.querySelectorAll('pre code').forEach((block) => {
                hljs.highlightElement(block);
            });
        }

        this.currentMessageId = null;
        this.currentMessageContent = '';
        this.scrollToBottom();
    }

    addMessageToUI(role, content, attachments = []) {
        const messageList = document.getElementById('messageList');
        const isUser = role === 'user';

        const imageAttachments = (attachments || [])
            .filter(a => (a.contentType || '').toLowerCase().startsWith('image/') && a.storagePath);

        const attachmentsHtml = imageAttachments.length > 0
            ? `
                <div class="message-attachments" style="display:flex; gap:8px; margin-top:8px; flex-wrap:wrap;">
                    ${imageAttachments.map(a => `
                        <a href="${a.storagePath}" target="_blank" rel="noopener noreferrer" title="${this.escapeHtml(a.fileName || 'image')}">
                            <img src="${a.storagePath}" alt="${this.escapeHtml(a.fileName || 'image')}" style="width: 84px; height: 84px; object-fit: cover; border-radius: 6px; border: 1px solid var(--border-color);" />
                        </a>
                    `).join('')}
                </div>
              `
            : '';

        const messageEl = document.createElement('div');
        messageEl.className = `message ${isUser ? 'message-user' : 'message-assistant'}`;
        messageEl.innerHTML = `
            <div class="message-avatar">${isUser ? 'Y' : this.currentAgent[0]}</div>
            <div class="message-content">
                <div class="message-header">
                    <span class="message-author">${isUser ? 'You' : this.currentAgent}</span>
                    <span class="message-time">${this.formatTime(new Date())}</span>
                </div>
                <div class="message-body">${isUser ? this.escapeHtml(content) : marked.parse(content)}</div>
                ${attachmentsHtml}
            </div>
        `;

        messageList.appendChild(messageEl);
        this.scrollToBottom();
    }

    createAssistantMessage(messageId) {
        const messageList = document.getElementById('messageList');

        const messageEl = document.createElement('div');
        messageEl.className = 'message message-assistant';
        messageEl.dataset.messageId = messageId;
        messageEl.innerHTML = `
            <div class="message-avatar">${this.currentAgent[0]}</div>
            <div class="message-content">
                <div class="message-header">
                    <span class="message-author">${this.currentAgent}</span>
                    <span class="message-time">${this.formatTime(new Date())}</span>
                </div>
                <div class="message-body"></div>
            </div>
        `;

        messageList.appendChild(messageEl);
    }

    updateMessageContent(messageId, content) {
        const contentEl = document.querySelector(`[data-message-id="${messageId}"] .message-body`);
        if (contentEl) {
            // Show plain text while streaming for performance
            contentEl.textContent = content;
            this.scrollToBottom();
        }
    }

    addSystemMessage(content, type = 'info') {
        const messageList = document.getElementById('messageList');
        const messageEl = document.createElement('div');
        messageEl.className = `message message-system message-${type}`;
        messageEl.innerHTML = `
            <div class="message-content" style="margin-left: 44px;">
                <div class="message-body">${this.escapeHtml(content)}</div>
            </div>
        `;
        messageList.appendChild(messageEl);
        this.scrollToBottom();
    }

    showTypingIndicator() {
        document.getElementById('typingIndicator').classList.remove('hidden');
        this.scrollToBottom();
    }

    hideTypingIndicator() {
        document.getElementById('typingIndicator').classList.add('hidden');
    }

    showToolNotification(toolName, status) {
        const notification = document.getElementById('toolNotification');
        notification.querySelector('.tool-name').textContent = toolName;
        notification.querySelector('.tool-status').textContent = status;
        notification.classList.remove('hidden');
    }

    hideToolNotification() {
        document.getElementById('toolNotification').classList.add('hidden');
    }

    showConnectionStatus(message) {
        // Could add a status bar in the UI
        console.log('Connection status:', message);
    }

    hideConnectionStatus() {
        console.log('Connection restored');
    }

    async selectAgent(agentName) {
        this.currentAgent = agentName;

        // Clear the chat UI and start fresh for the new agent
        this.clearMessageList();
        this.showWelcomeMessage();

        this.updateGitHubMcpVisibility();

        if (agentName === 'Code') {
            // Default to disconnected until we can confirm auth.
            if (!this.githubUsername && this.gitHubMcpBusyCount === 0) {
                this.setGitHubMcpStatus('disconnected');
            }
        }

        // Create a new session for the new agent so history stays separate
        await this.createSession();

        // When selecting the Code agent, check whether GitHub auth is available.
        // Don't block selection; GitHub MCP tools will simply be unavailable until connected.
        if (agentName === 'Code') {
            await this.checkGitHubAuth();
        }

        try {
            await this.connection.invoke('SelectAgent', agentName);
        } catch (error) {
            console.error('Select agent error:', error);
        }
        this.updateAgentDisplay({ name: agentName });
    }

    updateGitHubMcpVisibility() {
        const statusEl = document.getElementById('githubMcpStatus');
        if (!statusEl) return;
        statusEl.classList.toggle('hidden', this.currentAgent !== 'Code');
    }

    setGitHubMcpStatus(state, options = {}) {
        const statusEl = document.getElementById('githubMcpStatus');
        const textEl = document.getElementById('githubMcpStatusText');
        if (!statusEl || !textEl) return;

        statusEl.dataset.state = state;

        const username = options.username ?? this.githubUsername;

        switch (state) {
            case 'connected':
                textEl.textContent = username ? `Connected as ${username}` : 'Connected';
                break;
            case 'connecting':
                textEl.textContent = 'Connecting...';
                break;
            case 'busy':
                textEl.textContent = 'Busy...';
                break;
            case 'disconnected':
            default:
                textEl.textContent = 'Disconnected';
                break;
        }
    }

    async checkGitHubAuth() {
        try {
            this.setGitHubMcpStatus('connecting');
            const response = await fetch('/auth/github/status');
            const data = await response.json();

            if (!data.authenticated) {
                this.githubUsername = null;
                if (this.gitHubMcpBusyCount === 0) {
                    this.setGitHubMcpStatus('disconnected');
                }
                this.showGitHubAuthPrompt();
                return false;
            }

            this.githubUsername = data.username;
            if (this.gitHubMcpBusyCount === 0) {
                this.setGitHubMcpStatus('connected', { username: data.username });
            }
            return true;
        } catch (error) {
            console.error('GitHub auth check failed:', error);
            if (this.gitHubMcpBusyCount === 0 && this.currentAgent === 'Code') {
                this.setGitHubMcpStatus(this.githubUsername ? 'connected' : 'disconnected', {
                    username: this.githubUsername
                });
            }
            // Let the user proceed; MCP calls may still work without auth
            return true;
        }
    }

    showGitHubAuthPrompt() {
        // Remove any previous auth prompt
        document.querySelectorAll('.github-auth-prompt').forEach(el => el.remove());

        const messageList = document.getElementById('messageList');
        const promptEl = document.createElement('div');
        promptEl.className = 'message message-system github-auth-prompt';
        promptEl.innerHTML = `
            <div class="message-content" style="margin-left: 44px;">
                <div class="message-body">
                    <p><strong>GitHub Authentication Required</strong></p>
                    <p>The Code agent can use GitHub MCP tools, which requires access to your GitHub account to search repositories, issues, and pull requests.</p>
                    <a href="/auth/github/login?returnUrl=${encodeURIComponent('/')}" class="btn-github-auth">
                        <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor" style="vertical-align: middle; margin-right: 8px;">
                            <path fill-rule="evenodd" d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z"/>
                        </svg>
                        Connect GitHub Account
                    </a>
                </div>
            </div>
        `;
        messageList.appendChild(promptEl);
        this.scrollToBottom();
    }

    updateAgentDisplay(agentInfo) {
        document.getElementById('agentAvatar').textContent = agentInfo.name ? agentInfo.name[0] : 'A';
        document.getElementById('agentName').textContent = agentInfo.name || 'Unknown';
        document.getElementById('agentDescription').textContent = agentInfo.description || '';
    }

    renderSuggestedPrompts(agentName) {
        const container = document.getElementById('suggestedPromptsList');
        if (!container) return;

        const prompts = this.suggestedPrompts[agentName] || [];

        if (prompts.length === 0) {
            container.innerHTML = '';
            return;
        }

        container.innerHTML = prompts.map(p =>
            `<button type="button" class="suggested-prompt-btn" title="${this.escapeHtml(p)}">${this.escapeHtml(p)}</button>`
        ).join('');

        container.querySelectorAll('.suggested-prompt-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const input = document.getElementById('messageInput');
                input.value = btn.textContent;
                input.focus();
                this.sendMessage();
            });
        });
    }

    async createSession() {
        try {
            await this.connection.invoke('CreateSession');
        } catch (error) {
            console.error('Create session error:', error);
        }
    }

    async createNewSession() {
        this.clearMessageList();
        this.showWelcomeMessage();
        await this.createSession();
    }

    async clearSession() {
        if (this.sessionId) {
            try {
                await this.connection.invoke('ClearSession', this.sessionId);
            } catch (error) {
                console.error('Clear session error:', error);
            }
        }
    }

    clearMessageList() {
        const messageList = document.getElementById('messageList');
        messageList.innerHTML = '';
    }

    showWelcomeMessage() {
        const messageList = document.getElementById('messageList');
        messageList.innerHTML = `
            <div class="welcome-message">
                <div class="welcome-icon">
                    <svg width="48" height="48" viewBox="0 0 16 16" fill="currentColor">
                        <path d="M8 15c4.418 0 8-3.134 8-7s-3.582-7-8-7-8 3.134-8 7c0 1.76.743 3.37 1.97 4.6-.097 1.016-.417 2.13-.771 2.966-.079.186.074.394.273.362 2.256-.37 3.597-.938 4.18-1.234A9.06 9.06 0 0 0 8 15z"/>
                    </svg>
                </div>
                <h1>Welcome to AI Chat</h1>
                <p>Select an agent and start a conversation. Your messages will stream in real-time.</p>
                <div class="feature-list">
                    <div class="feature">
                        <span class="feature-icon">S</span>
                        <span>Streaming Responses</span>
                    </div>
                    <div class="feature">
                        <span class="feature-icon">A</span>
                        <span>Multiple Agents</span>
                    </div>
                    <div class="feature">
                        <span class="feature-icon">T</span>
                        <span>Tool Calling</span>
                    </div>
                    <div class="feature">
                        <span class="feature-icon">M</span>
                        <span>Markdown Support</span>
                    </div>
                </div>

                <div class="suggested-prompts" id="suggestedPrompts">
                    <h3 class="suggested-prompts-title">Try asking...</h3>
                    <div class="suggested-prompts-list" id="suggestedPromptsList"></div>
                </div>
            </div>
        `;
        this.renderSuggestedPrompts(this.currentAgent);
    }

    hideWelcomeMessage() {
        const welcome = document.querySelector('.welcome-message');
        if (welcome) {
            welcome.remove();
        }
    }

    async loadAgents() {
        try {
            const agents = await this.connection.invoke('GetAvailableAgents');
            this.agents = agents;
            const select = document.getElementById('agentSelect');
            select.innerHTML = agents.map(a =>
                `<option value="${a.name}">${a.name} - ${a.description}</option>`
            ).join('');

            if (agents.length > 0) {
                this.selectAgent(agents[0].name);
            }
        } catch (error) {
            console.error('Load agents error:', error);
        }
    }

    // Theme is intentionally fixed to dark for this demo.

    async handleFileSelect(files) {
        if (!files || files.length === 0) return;

        const filePreview = document.getElementById('filePreview');
        const previewList = filePreview.querySelector('.file-preview-list');

        for (const file of files) {
            const formData = new FormData();
            formData.append('file', file);

            try {
                const response = await fetch('/api/upload', {
                    method: 'POST',
                    body: formData
                });

                if (response.ok) {
                    const attachment = await response.json();
                    this.attachedFiles.push(attachment);

                    const item = document.createElement('div');
                    item.className = 'file-preview-item';

                    const isImage = (attachment.contentType || '').toLowerCase().startsWith('image/');
                    const previewHtml = isImage && attachment.storagePath
                        ? `<img src="${attachment.storagePath}" alt="${this.escapeHtml(attachment.fileName || 'image')}" style="width: 28px; height: 28px; object-fit: cover; border-radius: 4px; border: 1px solid var(--border-color);" />`
                        : '';

                    item.innerHTML = `
                        ${previewHtml}
                        <span>${attachment.fileName}</span>
                        <button type="button" onclick="chatApp.removeFile('${attachment.id}')">&times;</button>
                    `;
                    previewList.appendChild(item);
                    filePreview.classList.remove('hidden');
                }
            } catch (error) {
                console.error('File upload error:', error);
            }
        }

        // Clear file input
        document.getElementById('fileInput').value = '';
    }

    removeFile(fileId) {
        this.attachedFiles = this.attachedFiles.filter(f => f.id !== fileId);
        const filePreview = document.getElementById('filePreview');
        const previewList = filePreview.querySelector('.file-preview-list');

        // Re-render preview list
        previewList.innerHTML = this.attachedFiles.map(f => {
            const isImage = (f.contentType || '').toLowerCase().startsWith('image/') && f.storagePath;
            const previewHtml = isImage
                ? `<img src="${f.storagePath}" alt="${this.escapeHtml(f.fileName || 'image')}" style="width: 28px; height: 28px; object-fit: cover; border-radius: 4px; border: 1px solid var(--border-color);" />`
                : '';

            return `
                <div class="file-preview-item">
                    ${previewHtml}
                    <span>${this.escapeHtml(f.fileName || '')}</span>
                    <button type="button" onclick="chatApp.removeFile('${f.id}')">&times;</button>
                </div>
            `;
        }).join('');

        if (this.attachedFiles.length === 0) {
            filePreview.classList.add('hidden');
        }
    }

    scrollToBottom() {
        const messageList = document.getElementById('messageList');
        messageList.scrollTop = messageList.scrollHeight;
    }

    // ----------------------------------------------------------------
    // Single Page Mode
    // ----------------------------------------------------------------

    switchUiMode(mode) {
        this.uiMode = mode;
        const messageList = document.getElementById('messageList');
        const spContainer = document.getElementById('spContainer');

        if (mode === 'singlepage') {
            messageList.classList.add('hidden');
            spContainer.classList.remove('hidden');
            document.getElementById('messageInput').placeholder =
                'Type a prompt… (Enter to send, Shift+Enter for new line)';
        } else {
            messageList.classList.remove('hidden');
            spContainer.classList.add('hidden');
            document.getElementById('messageInput').placeholder =
                'Type a message… (Enter to send, Shift+Enter for new line)';
            this.hideSpContextMenu();
            this.hideInlinePrompt();
        }
    }

    // Navigation history ------------------------------------------

    addToSpHistory(prompt, content, title) {
        // Discard any forward history when a new page is generated
        if (this.spCurrentIndex < this.spHistory.length - 1) {
            this.spHistory = this.spHistory.slice(0, this.spCurrentIndex + 1);
        }
        this.spHistory.push({ prompt, content, title });
        this.spCurrentIndex = this.spHistory.length - 1;
        this.updateSpNavButtons();
    }

    navigateToSpPage(index) {
        if (index < 0 || index >= this.spHistory.length) return;
        this.spCurrentIndex = index;
        const page = this.spHistory[index];

        const contentEl = document.getElementById('spContent');
        if (contentEl) {
            contentEl.innerHTML = marked.parse(page.content);
            contentEl.querySelectorAll('pre code').forEach((block) => hljs.highlightElement(block));
            contentEl.scrollTop = 0;
        }

        this.updateSpBreadcrumb();
        this.updateSpNavButtons();
    }

    navigateBack() {
        if (this.spCurrentIndex > 0) {
            this.navigateToSpPage(this.spCurrentIndex - 1);
        }
    }

    navigateForward() {
        if (this.spCurrentIndex < this.spHistory.length - 1) {
            this.navigateToSpPage(this.spCurrentIndex + 1);
        }
    }

    updateSpBreadcrumb() {
        const breadcrumb = document.getElementById('spBreadcrumb');
        if (!breadcrumb) return;

        breadcrumb.innerHTML = this.spHistory.map((page, index) => {
            const isActive = index === this.spCurrentIndex;
            const label = this.escapeHtml(page.title);
            const sep = index < this.spHistory.length - 1
                ? '<span class="sp-breadcrumb-sep" aria-hidden="true">›</span>'
                : '';

            if (isActive) {
                return `<span class="sp-breadcrumb-item sp-breadcrumb-current" aria-current="page" title="${this.escapeHtml(page.prompt)}">${label}</span>${sep}`;
            }
            return `<button class="sp-breadcrumb-item sp-breadcrumb-link" data-index="${index}" title="${this.escapeHtml(page.prompt)}">${label}</button>${sep}`;
        }).join('');

        breadcrumb.querySelectorAll('.sp-breadcrumb-link').forEach(btn => {
            btn.addEventListener('click', () => {
                this.navigateToSpPage(parseInt(btn.dataset.index, 10));
            });
        });
    }

    updateSpNavButtons() {
        const backBtn = document.getElementById('spBackBtn');
        const forwardBtn = document.getElementById('spForwardBtn');
        if (backBtn) backBtn.disabled = this.spCurrentIndex <= 0;
        if (forwardBtn) forwardBtn.disabled = this.spCurrentIndex >= this.spHistory.length - 1;
    }

    // Context menu on text selection --------------------------------

    handleTextSelection(e) {
        if (this.uiMode !== 'singlepage') return;

        // Only process selection within sp-content
        const selection = window.getSelection();
        const text = selection ? selection.toString().trim() : '';

        if (text.length === 0) {
            this.hideSpContextMenu();
            return;
        }

        this.spSelectedText = text;

        // Position the menu near the end of the selection
        try {
            const range = selection.getRangeAt(0);
            const rect = range.getBoundingClientRect();
            this.showSpContextMenu(
                Math.round(rect.left + rect.width / 2),
                Math.round(rect.top)
            );
        } catch (_) {
            // If we can't get rect just show at cursor position
            this.showSpContextMenu(e.clientX || 0, e.clientY || 0);
        }
    }

    showSpContextMenu(x, y) {
        const menu = document.getElementById('spContextMenu');
        if (!menu) return;
        menu.classList.remove('hidden');

        // Temporarily show so we can measure its size
        menu.style.visibility = 'hidden';
        menu.style.left = '0';
        menu.style.top = '0';
        menu.style.transform = 'none';

        const menuW = menu.offsetWidth;
        const menuH = menu.offsetHeight;
        const vw = window.innerWidth;
        const vh = window.innerHeight;

        // Prefer above the selection
        let top = y - menuH - 8;
        if (top < 4) top = y + 8;
        if (top + menuH > vh - 4) top = vh - menuH - 4;

        let left = x - menuW / 2;
        if (left < 4) left = 4;
        if (left + menuW > vw - 4) left = vw - menuW - 4;

        menu.style.left = `${left}px`;
        menu.style.top = `${top}px`;
        menu.style.visibility = '';
    }

    hideSpContextMenu() {
        const menu = document.getElementById('spContextMenu');
        if (menu) menu.classList.add('hidden');
    }

    // "Prompt on selected" ----------------------------------------

    promptOnSelected() {
        this.hideSpContextMenu();
        const previewEl = document.getElementById('spSelectedPreview');
        if (previewEl) {
            const preview = this.spSelectedText.length > 90
                ? `"${this.spSelectedText.substring(0, 90)}…"`
                : `"${this.spSelectedText}"`;
            previewEl.textContent = preview;
        }
        const overlay = document.getElementById('spInlinePrompt');
        if (overlay) {
            overlay.classList.remove('hidden');
            const input = document.getElementById('spInlineInput');
            if (input) {
                input.value = '';
                input.focus();
            }
        }
    }

    hideInlinePrompt() {
        const overlay = document.getElementById('spInlinePrompt');
        if (overlay) overlay.classList.add('hidden');
        const input = document.getElementById('spInlineInput');
        if (input) input.value = '';
    }

    async submitInlinePrompt() {
        const input = document.getElementById('spInlineInput');
        const userQuestion = input ? input.value.trim() : '';
        if (!userQuestion) return;

        this.hideInlinePrompt();

        const prompt = `Regarding this text: "${this.spSelectedText}" — ${userQuestion}`;
        await this.sendSpPrompt(prompt);
    }

    // "More on this" ----------------------------------------------

    async moreOnThis() {
        this.hideSpContextMenu();
        const prompt = `Tell me more about: "${this.spSelectedText}"`;
        await this.sendSpPrompt(prompt);
    }

    // Send a sub-prompt in Single Page mode -----------------------

    async sendSpPrompt(prompt) {
        if (!this.isConnected) return;

        this.spCurrentPrompt = prompt;

        // Show loading state
        const contentEl = document.getElementById('spContent');
        if (contentEl) {
            contentEl.innerHTML = '<div class="sp-loading"><span class="sp-loading-text">Generating response…</span></div>';
        }

        // Discard forward history if we are not at the end
        if (this.spCurrentIndex < this.spHistory.length - 1) {
            this.spHistory = this.spHistory.slice(0, this.spCurrentIndex + 1);
        }

        this.showTypingIndicator();

        try {
            await this.connection.invoke('SendMessage', this.sessionId, this.currentAgent, prompt, []);
        } catch (error) {
            console.error('Send SP prompt error:', error);
            this.hideTypingIndicator();
            if (contentEl) {
                contentEl.innerHTML = `<p style="color:var(--error-color)">Failed to send: ${this.escapeHtml(error.message)}</p>`;
            }
        }
    }

    formatTime(date) {
        return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
}

// Initialize application when DOM is ready
let chatApp;
document.addEventListener('DOMContentLoaded', () => {
    chatApp = new ChatApp();
});
