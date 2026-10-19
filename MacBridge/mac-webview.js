(() => {
  window.magpieMac = true;
  const listeners = [];
  const pending = [];
  let busy = false;
  let sequence = 0;
  function flush() {
    if (busy || !pending.length) return;
    busy = true;
    window.location.href = 'magpie://message/' + pending.shift();
  }
  window.magpieHostAck = () => { busy = false; flush(); };
  window.magpieReceive = message => listeners.forEach(listener => listener({ data: message }));
  window.chrome = { webview: {
    postMessage(message) {
      const bytes = new TextEncoder().encode(JSON.stringify(message));
      if (bytes.length > 1024 * 1024) {
        window.magpieReceive({type:'error_message',payload:{text:'Message attachments are too large.'}});
        window.magpieReceive({type:'generation_state',payload:{isGenerating:false}});
        return;
      }
      let binary = '';
      for (let offset = 0; offset < bytes.length; offset += 8192)
        binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
      const encoded = btoa(binary).replace(/\+/g, '-').replace(/\//g, '_');
      const id = ++sequence;
      const total = Math.ceil(encoded.length / 16000);
      for (let index = 0; index < total; index++)
        pending.push(`${id}/${index}/${total}/${encoded.slice(index * 16000, (index + 1) * 16000)}`);
      flush();
    },
    addEventListener(type, listener) { if (type === 'message') listeners.push(listener); }
  }};
  document.addEventListener('DOMContentLoaded', () => {
    document.querySelector('.hint').textContent = '⌘ + Enter to send';
    // The backend currently accepts text attachments only.
    document.getElementById('file-input').accept = '.txt,.md,.json,.csv,.tsv,.log,.py,.cs,.js,.ts,.html,.css,.xml,.yml,.yaml,.ini,.toml,.sql,.sh';
    const python = document.getElementById('agent-service-command');
    document.querySelector('label[for="agent-service-command"]').textContent = 'Python executable';
    python.placeholder = '/path/to/Magpie/.venv/bin/python';
    document.getElementById('agent-service-workdir').placeholder = '/path/to/Magpie/agent_service';
    document.getElementById('agent-invoke-path').placeholder = '/graph/invoke/stream';
    for (const [id, name] of [['openai', 'OpenAI'], ['custom', 'OpenAI compatible']]) {
      const option = document.createElement('option'); option.value = id; option.textContent = name;
      document.getElementById('model-provider').appendChild(option);
    }
    document.getElementById('input').addEventListener('keydown', event => {
      if (event.key === 'Enter' && event.metaKey) {
        event.preventDefault(); document.getElementById('send').click();
      }
    });
  });
})();
