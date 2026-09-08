namespace Orchestrator;

public static class HtmlUi
{
    public const string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Commerce Multi-Agent Chat</title>
  <style>
    :root { color-scheme: light dark; }
    body { font-family: system-ui, sans-serif; max-width: 760px; margin: 0 auto; padding: 1rem; }
    h1 { font-size: 1.2rem; }
    #agents { font-size: .8rem; opacity: .8; margin-bottom: 1rem; }
    #log { border: 1px solid #8884; border-radius: 8px; padding: 1rem; min-height: 320px; }
    .msg { margin: .5rem 0; padding: .5rem .75rem; border-radius: 8px; white-space: pre-wrap; }
    .user { background: #3b82f622; }
    .assistant { background: #10b98122; }
    .role { font-size: .7rem; text-transform: uppercase; opacity: .6; }
    form { display: flex; gap: .5rem; margin-top: 1rem; }
    input { flex: 1; padding: .6rem; border-radius: 8px; border: 1px solid #8884; }
    button { padding: .6rem 1rem; border-radius: 8px; border: 0; background: #3b82f6; color: #fff; cursor: pointer; }
    button:disabled { opacity: .5; cursor: default; }
  </style>
</head>
<body>
  <h1>Commerce Multi-Agent Chat</h1>
  <div id="agents">Loading agents…</div>
  <div id="log"></div>
  <form id="form">
    <input id="input" placeholder="e.g. Is winter coat stock aligned with the active catalog?" autocomplete="off" />
    <button id="send" type="submit">Send</button>
  </form>

  <script>
    const log = document.getElementById('log');
    const form = document.getElementById('form');
    const input = document.getElementById('input');
    const send = document.getElementById('send');
    let threadId = null;

    function add(role, text) {
      const div = document.createElement('div');
      div.className = 'msg ' + role;
      div.innerHTML = '<div class="role">' + role + '</div>' + escapeHtml(text);
      log.appendChild(div);
      log.scrollTop = log.scrollHeight;
    }
    function escapeHtml(s) {
      return s.replace(/[&<>]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;'}[c]));
    }

    async function loadAgents() {
      try {
        const res = await fetch('/api/agents');
        const agents = await res.json();
        document.getElementById('agents').textContent =
          'Agents: ' + agents.map(a => (a.name || a.serviceName) + (a.isAvailable ? ' ✓' : ' ✗')).join('  |  ');
      } catch { document.getElementById('agents').textContent = 'Could not load agents.'; }
    }
    loadAgents();

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const msg = input.value.trim();
      if (!msg) return;
      add('user', msg);
      input.value = '';
      send.disabled = true;
      try {
        const res = await fetch('/api/chat', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ threadId, message: msg })
        });
        const data = await res.json();
        threadId = data.threadId;
        add('assistant', data.answer);
      } catch (err) {
        add('assistant', 'Error: ' + err);
      } finally {
        send.disabled = false;
        input.focus();
      }
    });
  </script>
</body>
</html>
""";
}
