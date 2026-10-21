#!/usr/bin/env node
// Hook entry point for spidey, dispatched on hook_event_name:
//   PermissionRequest  remember the tool call so the visit can show the real command
//   Notification       permission prompt / question -> he drops in (spider-sense if risky)
//   UserPromptSubmit   note when the turn started
//   Stop               a long turn finished while you were away -> "done" visit
// Manual test: node launch.js --test [wait|alert|done]
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

if (process.platform !== 'win32' && require.main === module) process.exit(0);

const DEFAULTS = {
  suit: 'random',        // suit subfolder, or "random"
  zoom: 0,               // whole-number sprite scale; 0 = auto from screen DPI
  fps: 12,               // sprite animation speed
  sound: true,
  volume: 0.6,           // 0..1
  spiderSense: true,     // warn about risky commands
  doneAfterSeconds: 120, // "done" visit for turns at least this long; 0 = off
};

// Risky shell commands -> what the bubble says ("Careful! It ...").
const RISKS = [
  [/\brm\s+(-\S+\s+)*(-[a-zA-Z]*[rR][a-zA-Z]*|--recursive)(\s|$)/, 'deletes files recursively'],
  [/\bRemove-Item\b.*-Recurse/i, 'deletes files recursively'],
  [/\b(rd|rmdir|del|erase)\b.*\s\/s\b/i, 'deletes files recursively'],
  [/\bgit\s+push\b.*(\s--force\b|\s-f\b|--force-with-lease)/i, 'force-pushes git history'],
  [/\bgit\s+reset\s+--hard\b/i, 'throws away uncommitted work'],
  [/\bgit\s+clean\s+-[a-z]*f/i, 'deletes untracked files'],
  [/\bgit\s+branch\s+-D\b/, 'force-deletes a branch'],
  [/\b(DROP\s+(TABLE|DATABASE|SCHEMA)|TRUNCATE\s+TABLE)\b/i, 'drops database data'],
  [/\bDELETE\s+FROM\s+[\w."`[\]]+\s*(;|"|'|$)/i, 'deletes every row (no WHERE)'],
  [/\b(curl|wget)\b[^|]*\|\s*(sudo\s+)?(ba|z)?sh\b/i, 'runs a script from the internet'],
  [/\b(iwr|irm|Invoke-WebRequest|Invoke-RestMethod)\b[^|]*\|\s*(iex|Invoke-Expression)\b/i, 'runs a script from the internet'],
  [/\bformat\s+[a-z]:/i, 'formats a drive'],
  [/\b(mkfs|diskpart)\b|\bdd\s+if=/i, 'writes to a disk directly'],
  [/\bchmod\s+(-R\s+)?777\b/, 'opens up file permissions'],
];
const SECRET_FILE = /(^|[\\/])\.env(\.[\w-]+)?$|id_(rsa|ed25519)|\.(pem|key|pfx)$|credentials/i;

const tmp = (kind, sid) =>
  path.join(os.tmpdir(), `claude-spidey-${kind}-${String(sid || 'none').replace(/[^\w-]/g, '')}.json`);
const readJson = (file) => { try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; } };
const writeJson = (file, data) => { try { fs.writeFileSync(file, JSON.stringify(data)); } catch {} };
const remove = (file) => { try { fs.unlinkSync(file); } catch {} };
const oneLine = (s, max = 140) => {
  const t = String(s).replace(/\s+/g, ' ').trim();
  return t.length > max ? t.slice(0, max - 1) + '…' : t;
};

// Per-user folder: its sprites (if any) and config.json override the bundled ones.
const userDir = process.env.CLAUDE_SPIDEY_SPRITES || path.join(os.homedir(), '.claude', 'spidey');
const bundledDir = path.join(__dirname, '..', 'sprites');

// True if dir holds hanging frames directly or in suit subfolders.
function hasSprites(dir) {
  const frames = (d) => { try { return fs.readdirSync(d).some((f) => /^Selectscreen_.*\.png$/i.test(f)); } catch { return false; } };
  if (frames(dir)) return true;
  try {
    return fs.readdirSync(dir, { withFileTypes: true }).some((e) => e.isDirectory() && frames(path.join(dir, e.name)));
  } catch { return false; }
}

function spriteDir() {
  return hasSprites(userDir) ? userDir : bundledDir;
}

function loadConfig() {
  return {
    ...DEFAULTS,
    ...(readJsonText(path.join(bundledDir, 'config.json')) || {}),
    ...(readJsonText(path.join(userDir, 'config.json')) || {}),
  };
}

function readJsonText(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^﻿/, '')); } catch { return null; }
}

// What the bubble shows about a tool call, and whether it's risky.
function describe(tool, input = {}) {
  if (typeof input.command === 'string') {
    const hit = RISKS.find(([re]) => re.test(input.command));
    return { detail: '$ ' + oneLine(input.command), risk: hit && hit[1] };
  }
  const file = input.file_path || input.notebook_path;
  if (file) {
    return { detail: `${tool}  ${oneLine(file)}`, risk: SECRET_FILE.test(file) ? 'touches a secrets file' : null };
  }
  if (input.url) return { detail: `${tool}  ${oneLine(input.url)}`, risk: null };
  return { detail: '', risk: null };
}

const duration = (secs) => {
  const m = Math.floor(secs / 60), s = Math.round(secs % 60);
  return m ? `${m}m ${s}s` : `${s}s`;
};

// First line of Claude's last reply, read from the end of the transcript.
function lastReply(transcript) {
  try {
    const fd = fs.openSync(transcript, 'r');
    const size = fs.fstatSync(fd).size;
    const len = Math.min(size, 256 * 1024);
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, size - len);
    fs.closeSync(fd);
    const lines = buf.toString('utf8').split('\n').reverse();
    for (const line of lines) {
      let entry;
      try { entry = JSON.parse(line); } catch { continue; }
      const content = entry.type === 'assistant' && entry.message && entry.message.content;
      if (!Array.isArray(content)) continue;
      const text = content.filter((c) => c.type === 'text').map((c) => c.text).join('\n').trim();
      const first = text.split('\n').find((l) => l.replace(/[#*_`>\s-]/g, '').length > 0);
      if (first) return oneLine(first.replace(/[#*_`]/g, ''), 120);
    }
  } catch {}
  return '';
}

function main(payload, test) {
  const cfg = loadConfig();
  const sid = payload.session_id;

  if (test) {
    const samples = {
      wait: { title: 'Hey! Claude needs your OK', message: 'Claude needs your permission to use Bash', detail: '$ npm install express' },
      alert: { title: 'Careful! It force-pushes git history', message: 'Claude needs your permission to use Bash', detail: '$ git push --force origin main' },
      done: { title: 'All done!', message: 'Finished in 3m 12s', detail: 'Refactored the auth flow and all tests pass.' },
    };
    return show(cfg, payload, { mode: test, ...samples[test] }, true);
  }

  switch (payload.hook_event_name) {
    case 'PermissionRequest':
      writeJson(tmp('perm', sid), { at: Date.now(), tool: payload.tool_name, input: payload.tool_input });
      return process.exit(0);

    case 'UserPromptSubmit':
      writeJson(tmp('turn', sid), { at: Date.now() });
      return process.exit(0);

    case 'Stop': {
      const turn = readJson(tmp('turn', sid));
      remove(tmp('turn', sid));
      const secs = turn ? (Date.now() - turn.at) / 1000 : 0;
      if (!cfg.doneAfterSeconds || secs < cfg.doneAfterSeconds) return process.exit(0);
      return show(cfg, payload, {
        mode: 'done', title: 'All done!', message: `Finished in ${duration(secs)}`,
        detail: lastReply(payload.transcript_path),
      });
    }

    case 'Notification': {
      if (payload.notification_type === 'elicitation_dialog') {
        return show(cfg, payload, { mode: 'wait', title: 'Claude has a question for you', message: payload.message || '' });
      }
      if (payload.notification_type !== 'permission_prompt') return process.exit(0);
      const perm = readJson(tmp('perm', sid));
      remove(tmp('perm', sid));
      const { detail, risk } = perm && Date.now() - perm.at < 120000
        ? describe(perm.tool, perm.input) : { detail: '', risk: null };
      const message = payload.message || 'Claude needs your permission to continue';
      if (risk && cfg.spiderSense) {
        return show(cfg, payload, { mode: 'alert', title: `Careful! It ${risk}`, message, detail });
      }
      return show(cfg, payload, { mode: 'wait', title: 'Hey! Claude needs your OK', message, detail });
    }
  }
  process.exit(0);
}

function show(cfg, payload, visit, test = false) {
  const options = {
    Mode: visit.mode,
    Title: visit.title,
    Message: visit.message || '',
    Detail: visit.detail || '',
    // Lets the overlay tell apart several editor windows owned by one process.
    Project: path.basename(payload.cwd || process.cwd()),
    SpriteDir: spriteDir(),
    Suit: String(cfg.suit),
    Zoom: Number(cfg.zoom) | 0,
    Fps: Number(cfg.fps) || 12,
    SoundDir: path.join(__dirname, '..', 'assets'),
    Sound: Boolean(cfg.sound),
    Volume: Number(cfg.volume),
    Always: test || process.env.CLAUDE_SPIDEY_ALWAYS === '1',
    Timeout: test ? 15 : visit.mode === 'done' ? 20 : 180,
  };

  // libuv kills non-detached children when node exits, but a detached
  // powershell.exe exits immediately. So: detached node keeper -> normal powershell.
  const ready = path.join(os.tmpdir(), `claude-spidey-ready-${process.pid}`);
  const args = [
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
    '-File', path.join(__dirname, 'spidey.ps1'),
    '-StartPid', String(process.pid),
    '-Options64', Buffer.from(JSON.stringify(options), 'utf8').toString('base64'),
    '-ReadyFile', ready,
  ];
  const child = spawn(process.execPath, [__filename, '--keeper', JSON.stringify(args)], {
    detached: true,
    windowsHide: true,
    stdio: 'ignore',
  });

  // Stay alive until the overlay has captured our ancestor chain.
  const done = () => {
    remove(ready);
    child.unref();
    process.exit(0);
  };
  child.on('exit', done);
  child.on('error', done);
  setInterval(() => { if (fs.existsSync(ready)) done(); }, 50);
  setTimeout(done, 5000);
}

module.exports = { describe };

const keeper = process.argv.indexOf('--keeper');
const testIndex = process.argv.indexOf('--test');
if (require.main !== module) {
  // required for tests
} else if (keeper !== -1) {
  const args = JSON.parse(process.argv[keeper + 1]);
  spawn('powershell.exe', args, { windowsHide: true, stdio: 'ignore' })
    .on('exit', () => process.exit(0))
    .on('error', () => process.exit(1));
} else if (testIndex !== -1) {
  const mode = ['wait', 'alert', 'done'].includes(process.argv[testIndex + 1]) ? process.argv[testIndex + 1] : 'wait';
  main({}, mode);
} else {
  let input = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (d) => { input += d; });
  process.stdin.on('end', () => {
    let payload = {};
    try { payload = JSON.parse(input.replace(/^﻿/, '')); } catch {}
    main(payload, null);
  });
}
