(function() {
  var vsc = acquireVsCodeApi();
  // Expose to v2 glue (chat-v2.js). acquireVsCodeApi() can only be called once
  // per webview, and chat.js loads first, so we hand the handle out here.
  window.__solvraVscode = vsc;
  var messagesEl = document.getElementById('messages');
  var emptyState = document.getElementById('empty-state');
  var inputEl = document.getElementById('input');
  var sendBtn = document.getElementById('send-btn');
  var cancelBtn = document.getElementById('cancel-btn');
  var progressEl = document.getElementById('progress');
  var progressLabel = document.getElementById('progress-label');
  var acEl = document.getElementById('autocomplete');
  var chipsEl = document.getElementById('file-chips');
  var charCount = document.getElementById('char-count');
  var isRunning = false;
  var showThinking = false;
  var streamDiv = null;
  var streamText = '';
  var renderPending = false;

  // Autocomplete state
  var acMode = null;
  var acItems = [];
  var acSelected = 0;
  var acSearchTimeout = null;
  var attachedFiles = {};

  var slashCommands = [
    { name: 'model', desc: 'Switch model', args: '<model-name>' },
    { name: 'provider', desc: 'Switch provider', args: '<name>' },
    { name: 'clear', desc: 'Clear chat session' },
    { name: 'help', desc: 'Show available commands' },
    { name: 'effort', desc: 'Set effort level', args: '<low|medium|high|max>' },
    { name: 'tools', desc: 'List available tools' },
    { name: 'sessions', desc: 'List past sessions' },
    { name: 'memory', desc: 'Search memory', args: '<query>' },
    { name: 'compact', desc: 'Compact conversation history' }
  ];

  // ── Header buttons (only present in sidebar view) ──
  var newSessionBtn = document.getElementById('new-session-btn');
  var openTabBtn = document.getElementById('open-tab-btn');
  if (newSessionBtn) {
    newSessionBtn.addEventListener('click', function() {
      vsc.postMessage({ type: 'newSession' });
    });
  }
  if (openTabBtn) {
    openTabBtn.addEventListener('click', function() {
      vsc.postMessage({ type: 'openInTab' });
    });
  }

  function setRunning(r) {
    isRunning = r;
    sendBtn.style.display = r ? 'none' : '';
    cancelBtn.style.display = r ? '' : 'none';
    inputEl.disabled = r;
    if (r) {
      progressEl.classList.add('visible');
      progressLabel.textContent = 'Thinking...';
    } else {
      progressEl.classList.remove('visible');
    }
  }

  function hideEmpty() {
    if (emptyState) emptyState.style.display = 'none';
  }

  function scrollBottom() {
    messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function escapeHtml(t) {
    var d = document.createElement('div');
    d.textContent = t;
    return d.innerHTML;
  }

  // ── Markdown rendering ──
  function renderMd(text) {
    if (!text) return '';
    var lines = text.split('\n');
    var html = '';
    var inCode = false;
    var codeLines = [];
    var inThinking = false;
    var thinkLines = [];
    var inTool = false;
    var toolLines = [];
    var toolName = '';

    for (var li = 0; li < lines.length; li++) {
      var line = lines[li];

      // Detect thinking blocks
      if (!inCode && line.match(/^<thinking>/i)) {
        inThinking = true;
        thinkLines = [];
        continue;
      }
      if (inThinking && line.match(/^<\/thinking>/i)) {
        inThinking = false;
        html += buildCollapsible('thinking', 'Thinking', thinkLines.join('\n'));
        continue;
      }
      if (inThinking) { thinkLines.push(line); continue; }

      // Detect tool-use blocks
      if (!inCode && line.match(/^\[tool:/i)) {
        inTool = true;
        toolName = line.replace(/^\[tool:\s*/i, '').replace(/\]$/, '').trim() || 'Tool';
        toolLines = [];
        continue;
      }
      if (!inCode && line.match(/^\[Tool\]/i)) {
        inTool = true;
        toolName = 'Tool';
        toolLines = [];
        continue;
      }
      if (inTool && (line.match(/^\[\/(tool|Tool)\]/i) || line === '---' || line === '===')) {
        inTool = false;
        html += buildCollapsible('tool', toolName, toolLines.join('\n'));
        continue;
      }
      if (inTool) { toolLines.push(line); continue; }

      // Code fences
      if (!inCode && line.match(/^```/)) {
        inCode = true;
        codeLines = [];
        continue;
      }
      if (inCode && line.match(/^```/)) {
        inCode = false;
        html += '<pre><code>' + escapeHtml(codeLines.join('\n')) + '</code></pre>';
        continue;
      }
      if (inCode) { codeLines.push(line); continue; }

      // Regular markdown
      var h = escapeHtml(line);
      h = h.replace(/`([^`]+)`/g, '<code>$1</code>');
      h = h.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
      h = h.replace(/\*(.+?)\*/g, '<em>$1</em>');
      if (/^#### /.test(h)) { html += '<h4>' + h.slice(5) + '</h4>'; continue; }
      if (/^### /.test(h)) { html += '<h3>' + h.slice(4) + '</h3>'; continue; }
      if (/^## /.test(h)) { html += '<h2>' + h.slice(3) + '</h2>'; continue; }
      if (/^# /.test(h)) { html += '<h1>' + h.slice(2) + '</h1>'; continue; }
      if (/^---$/.test(h)) { html += '<hr>'; continue; }
      if (/^&gt; /.test(h)) { html += '<blockquote>' + h.slice(5) + '</blockquote>'; continue; }
      if (/^[-*] /.test(h)) { html += '<li>' + h.slice(2) + '</li>'; continue; }
      if (/^\d+\. /.test(h)) { html += '<li>' + h.replace(/^\d+\.\s*/, '') + '</li>'; continue; }
      if (h === '') { html += '<br>'; continue; }
      html += '<p>' + h + '</p>';
    }
    if (inCode) { html += '<pre><code>' + escapeHtml(codeLines.join('\n')) + '</code></pre>'; }
    if (inThinking && thinkLines.length) { html += buildCollapsible('thinking', 'Thinking...', thinkLines.join('\n')); }
    if (inTool && toolLines.length) { html += buildCollapsible('tool', toolName, toolLines.join('\n')); }
    html = html.replace(/(<li>.*?<\/li>)+/g, '<ul>$&</ul>');
    return html;
  }

  function buildCollapsible(type, label, content) {
    var icon = type === 'thinking' ? '&#x1f4ad;' : '&#x1f527;';
    var safeContent = escapeHtml(content);
    return '<div class="collapsible" onclick="this.classList.toggle(\'expanded\')">' +
      '<div class="collapsible-header">' +
        '<span class="collapsible-chevron">&#x25b6;</span>' +
        '<span class="collapsible-icon">' + icon + '</span>' +
        '<span class="collapsible-label">' + escapeHtml(label) + '</span>' +
        '<span class="collapsible-badge">' + type + '</span>' +
      '</div>' +
      '<div class="collapsible-body"><pre style="white-space:pre-wrap;margin:0;background:transparent;border:none;padding:0;font-size:12px;">' + safeContent + '</pre></div>' +
    '</div>';
  }

  function addMsg(role, text) {
    hideEmpty();
    var div = document.createElement('div');
    div.className = 'msg msg-' + role;
    if (role === 'assistant' || role === 'error') {
      div.innerHTML = renderMd(text);
    } else {
      div.innerHTML = '<p>' + escapeHtml(text) + '</p>';
    }
    messagesEl.appendChild(div);
    scrollBottom();
    return div;
  }

  // ── Input auto-resize ──
  function autoResize() {
    inputEl.style.height = 'auto';
    var h = Math.min(inputEl.scrollHeight, 200);
    inputEl.style.height = h + 'px';
    // Update char count
    if (charCount) {
      var len = inputEl.value.length;
      charCount.textContent = len > 0 ? len + '' : '';
    }
  }

  // ── Throttled stream rendering ──
  function scheduleRender() {
    if (renderPending) return;
    renderPending = true;
    requestAnimationFrame(function() {
      renderPending = false;
      if (streamDiv && streamText) {
        streamDiv.innerHTML = renderMd(streamText);
        scrollBottom();
      }
    });
  }

  // ── Autocomplete ──
  function showAc(items, mode) {
    if (!acEl) return;
    acItems = items;
    acMode = mode;
    acSelected = 0;
    if (!items.length) { hideAc(); return; }
    acEl.innerHTML = '';
    items.forEach(function(item, i) {
      var div = document.createElement('div');
      div.className = 'ac-item' + (i === 0 ? ' selected' : '');
      div.setAttribute('data-index', String(i));
      if (mode === 'slash') {
        div.innerHTML = '<span class="ac-icon">/</span><span class="ac-name">' + escapeHtml(item.name) + '</span>' +
          (item.args ? ' <span style="opacity:0.4">' + escapeHtml(item.args) + '</span>' : '') +
          '<span class="ac-desc">' + escapeHtml(item.desc) + '</span>';
      } else {
        div.innerHTML = '<span class="ac-icon">@</span><span class="ac-name">' + escapeHtml(item.relative) + '</span>';
      }
      acEl.appendChild(div);
    });
    acEl.style.display = 'block';
  }

  if (acEl) {
    acEl.addEventListener('mousedown', function(e) {
      e.preventDefault();
      e.stopPropagation();
    });
    acEl.addEventListener('click', function(e) {
      var target = e.target;
      while (target && target !== acEl) {
        if (target.classList && target.classList.contains('ac-item')) {
          var idx = parseInt(target.getAttribute('data-index'), 10);
          if (!isNaN(idx)) selectAcItem(idx);
          return;
        }
        target = target.parentElement;
      }
    });
  }

  function hideAc() {
    if (acEl) acEl.style.display = 'none';
    acMode = null;
    acItems = [];
    acSelected = 0;
  }

  function highlightAc(idx) {
    if (!acEl) return;
    if (idx < 0) idx = 0;
    if (idx >= acItems.length) idx = acItems.length - 1;
    var children = acEl.children;
    for (var i = 0; i < children.length; i++) {
      children[i].classList.toggle('selected', i === idx);
    }
    acSelected = idx;
    if (children[idx]) children[idx].scrollIntoView({ block: 'nearest' });
  }

  function selectAcItem(idx) {
    var item = acItems[idx];
    if (!item) return;
    if (!item.path && !item.name) { hideAc(); return; }
    var val = inputEl.value;
    if (acMode === 'slash') {
      var cmdText = '/' + item.name;
      hideAc();
      var existingMatch = val.match(new RegExp('^\\/(' + item.name + ')\\s+(.+)$'));
      var args = existingMatch ? existingMatch[2] : '';
      addMsg('user', cmdText + (args ? ' ' + args : ''));
      vsc.postMessage({ type: 'slashCommand', command: item.name, args: args });
      inputEl.value = '';
      autoResize();
      inputEl.focus();
      return;
    } else if (acMode === 'file' && item.path) {
      var atIdx = val.lastIndexOf('@');
      if (atIdx >= 0) {
        inputEl.value = val.substring(0, atIdx) + '@' + item.relative + ' ';
        vsc.postMessage({ type: 'readFile', path: item.path });
      }
    }
    hideAc();
    inputEl.focus();
  }

  // ── File chips ──
  function renderChips() {
    if (!chipsEl) return;
    chipsEl.innerHTML = '';
    var paths = Object.keys(attachedFiles);
    if (!paths.length) return;
    paths.forEach(function(p) {
      var chip = document.createElement('span');
      chip.className = 'file-chip';
      chip.innerHTML = '<span>' + escapeHtml(attachedFiles[p].relative) + '</span><span class="chip-remove" data-path="' + escapeHtml(p) + '">&times;</span>';
      chipsEl.appendChild(chip);
    });
    chipsEl.querySelectorAll('.chip-remove').forEach(function(el) {
      el.addEventListener('click', function() {
        delete attachedFiles[el.getAttribute('data-path')];
        renderChips();
      });
    });
  }

  // ── Input autocomplete check ──
  function getSlashMatch(text) {
    return text.match(/^\/([a-zA-Z]*)$/);
  }
  function getAtMatch(text) {
    return text.match(/@([^\s@]+)$/);
  }

  function checkAutocomplete() {
    var val = inputEl.value;
    var cursorPos = inputEl.selectionStart;
    var textBefore = val.substring(0, cursorPos);

    var sm = getSlashMatch(textBefore);
    if (sm) {
      var q = sm[1].toLowerCase();
      var filtered = slashCommands.filter(function(c) { return !q || c.name.indexOf(q) === 0; });
      showAc(filtered, 'slash');
      return;
    }

    var am = getAtMatch(textBefore);
    if (am && am[1].length >= 1) {
      clearTimeout(acSearchTimeout);
      acSearchTimeout = setTimeout(function() {
        vsc.postMessage({ type: 'searchFiles', query: am[1] });
      }, 200);
      return;
    }

    if (textBefore.endsWith('@')) {
      showAc([{ relative: 'Type a filename...', path: '' }], 'file');
      return;
    }

    hideAc();
  }

  inputEl.addEventListener('input', function() {
    autoResize();
    checkAutocomplete();
  });

  inputEl.addEventListener('keydown', function(e) {
    if (acMode && acItems.length > 0 && acEl && acEl.style.display !== 'none') {
      if (e.key === 'ArrowDown') { e.preventDefault(); highlightAc(acSelected + 1); return; }
      if (e.key === 'ArrowUp') { e.preventDefault(); highlightAc(acSelected - 1); return; }
      if (e.key === 'Tab' || (e.key === 'Enter' && !e.shiftKey)) {
        e.preventDefault(); selectAcItem(acSelected); return;
      }
      if (e.key === 'Escape') { e.preventDefault(); hideAc(); return; }
    }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
  });

  document.addEventListener('click', function(e) {
    if (acEl && !acEl.contains(e.target) && e.target !== inputEl) { hideAc(); }
  });

  function send() {
    var text = inputEl.value.trim();
    if (!text || isRunning) return;
    hideAc();

    var scMatch = text.match(/^\/([a-zA-Z]+)(?:\s+(.*))?$/);
    if (scMatch) {
      addMsg('user', text);
      vsc.postMessage({ type: 'slashCommand', command: scMatch[1], args: scMatch[2] || '' });
      inputEl.value = '';
      autoResize();
      return;
    }

    var fileRefs = Object.keys(attachedFiles);
    if (fileRefs.length) {
      fileRefs.forEach(function(p) {
        vsc.postMessage({ type: 'attachFile', path: p, relative: attachedFiles[p].relative, content: attachedFiles[p].content });
      });
    }

    addMsg('user', text + (fileRefs.length ? ' [' + fileRefs.length + ' file(s)]' : ''));
    vsc.postMessage({ type: 'sendMessage', text: text });
    inputEl.value = '';
    attachedFiles = {};
    renderChips();
    autoResize();
  }

  sendBtn.addEventListener('click', send);
  cancelBtn.addEventListener('click', function() { vsc.postMessage({ type: 'cancel' }); });

  // ── Message handler ──
  window.addEventListener('message', function(event) {
    var msg = event.data;
    switch (msg.type) {
      case 'addUserMessage':
        addMsg('user', msg.text);
        break;
      case 'addAssistantMessage':
        if (streamDiv) { streamDiv = null; streamText = ''; }
        addMsg('assistant', msg.text);
        break;
      case 'addErrorMessage':
        addMsg('error', msg.text);
        break;
      case 'streamChunk':
        hideEmpty();
        if (!streamDiv) {
          streamDiv = document.createElement('div');
          streamDiv.className = 'msg msg-assistant';
          messagesEl.appendChild(streamDiv);
          streamText = '';
          progressLabel.textContent = 'Generating...';
        }
        streamText += msg.text;
        scheduleRender();
        break;
      case 'streamEnd':
        streamDiv = null;
        streamText = '';
        renderPending = false;
        break;
      case 'runStarted':
        setRunning(true);
        break;
      case 'runComplete':
        setRunning(false);
        break;
      case 'focusInput':
        inputEl.focus();
        break;
      case 'clearMessages':
        messagesEl.innerHTML = '';
        if (emptyState) {
          messagesEl.appendChild(emptyState);
          emptyState.style.display = '';
        }
        streamDiv = null;
        streamText = '';
        attachedFiles = {};
        renderChips();
        break;
      case 'toggleThinking':
        showThinking = msg.show;
        document.querySelectorAll('.collapsible').forEach(function(el) {
          if (msg.show) el.classList.add('expanded');
          else el.classList.remove('expanded');
        });
        break;
      case 'fileResults':
        if (msg.files && msg.files.length) {
          showAc(msg.files, 'file');
        } else {
          showAc([{ relative: '(no files found)', path: '' }], 'file');
        }
        break;
      case 'fileContent':
        if (msg.path && msg.content) {
          attachedFiles[msg.path] = { relative: msg.relative, content: msg.content };
          renderChips();
        }
        break;
    }
  });

  // Focus input on load
  inputEl.focus();
})();
