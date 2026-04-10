(function () {
    'use strict';

    let isOpen = false;
    let isLoading = false;
    let conversationHistory = [];
    let panel, messagesContainer, textarea, sendBtn, errorEl;

    function init() {
        fetch('/umbraco/surface/AiChat/GetStatus')
            .then(r => r.json())
            .then(data => {
                if (data.enabled && data.loggedIn) {
                    createWidget();
                }
            })
            .catch(() => {});
    }

    function createWidget() {
        // Toggle button
        const btn = document.createElement('button');
        btn.className = 'chat-toggle-btn';
        btn.title = 'Fråga AI-assistenten';
        btn.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>';
        btn.addEventListener('click', togglePanel);
        document.body.appendChild(btn);

        // Panel
        panel = document.createElement('div');
        panel.className = 'chat-panel';
        panel.innerHTML = `
            <div class="chat-panel-header">
                <h6>Fråga om pistol.nu</h6>
                <button class="chat-panel-close" title="Stäng">&times;</button>
            </div>
            <div class="chat-messages"></div>
            <div class="chat-error" style="display:none"></div>
            <div class="chat-input-area">
                <textarea rows="1" placeholder="Ställ en fråga..." maxlength="2000"></textarea>
                <button type="button">Skicka</button>
            </div>
        `;
        document.body.appendChild(panel);

        messagesContainer = panel.querySelector('.chat-messages');
        textarea = panel.querySelector('textarea');
        sendBtn = panel.querySelector('.chat-input-area button');
        errorEl = panel.querySelector('.chat-error');

        panel.querySelector('.chat-panel-close').addEventListener('click', togglePanel);
        sendBtn.addEventListener('click', sendMessage);
        textarea.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                sendMessage();
            }
        });

        // Auto-resize textarea
        textarea.addEventListener('input', function () {
            this.style.height = 'auto';
            this.style.height = Math.min(this.scrollHeight, 80) + 'px';
        });

        // Welcome message
        addMessage('ai', 'Hej! Jag kan hjälpa dig med frågor om pistol.nu. Vad undrar du?');
    }

    function togglePanel() {
        isOpen = !isOpen;
        panel.classList.toggle('open', isOpen);
        if (isOpen) {
            textarea.focus();
        }
    }

    function addMessage(role, content) {
        const div = document.createElement('div');
        div.className = 'chat-msg ' + (role === 'user' ? 'chat-msg-user' : 'chat-msg-ai');

        if (role === 'ai') {
            div.innerHTML = formatMarkdown(content);
        } else {
            div.textContent = content;
        }

        messagesContainer.appendChild(div);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
        return div;
    }

    function showTyping() {
        const div = document.createElement('div');
        div.className = 'chat-msg-typing';
        div.id = 'chat-typing';
        div.innerHTML = '<span></span><span></span><span></span>';
        messagesContainer.appendChild(div);
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
    }

    function hideTyping() {
        const el = document.getElementById('chat-typing');
        if (el) el.remove();
    }

    function showError(msg) {
        errorEl.textContent = msg;
        errorEl.style.display = 'block';
        setTimeout(() => { errorEl.style.display = 'none'; }, 5000);
    }

    async function sendMessage() {
        const msg = textarea.value.trim();
        if (!msg || isLoading) return;

        errorEl.style.display = 'none';
        textarea.value = '';
        textarea.style.height = 'auto';
        addMessage('user', msg);

        isLoading = true;
        sendBtn.disabled = true;
        showTyping();

        try {
            var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
            var headers = { 'Content-Type': 'application/json' };
            if (tokenEl) headers['RequestVerificationToken'] = tokenEl.value;

            const response = await fetch('/umbraco/surface/AiChat/SendMessage', {
                method: 'POST',
                headers: headers,
                body: JSON.stringify({
                    message: msg,
                    history: conversationHistory.slice(-10)
                })
            });

            const data = await response.json();
            hideTyping();

            if (data.success) {
                addMessage('ai', data.response);
                conversationHistory.push({ role: 'user', content: msg });
                conversationHistory.push({ role: 'assistant', content: data.response });
            } else {
                showError(data.message || 'Ett fel uppstod.');
            }
        } catch (err) {
            hideTyping();
            showError('Kunde inte nå servern. Försök igen.');
        }

        isLoading = false;
        sendBtn.disabled = false;
        textarea.focus();
    }

    function formatMarkdown(text) {
        // Basic markdown: bold, lists, paragraphs
        let html = text
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');

        // Bold
        html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');

        // Unordered lists
        html = html.replace(/^- (.+)$/gm, '<li>$1</li>');
        html = html.replace(/(<li>.*<\/li>\n?)+/g, '<ul>$&</ul>');

        // Ordered lists
        html = html.replace(/^\d+\. (.+)$/gm, '<li>$1</li>');

        // Paragraphs
        html = html.replace(/\n\n/g, '</p><p>');
        html = '<p>' + html + '</p>';
        html = html.replace(/<p>\s*<\/p>/g, '');

        return html;
    }

    // Init when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
