#!/usr/bin/env node
// Synthesizes spidey's default sound effects into ../assets (original sounds,
// safe to ship). Re-run after tweaking: node make-sounds.js
const fs = require('fs');
const path = require('path');

const RATE = 22050;

// Seeded noise so the output is identical on every run.
let seed = 1234567;
const noise = () => ((seed = (seed * 1103515245 + 12345) >>> 0) / 0xffffffff) * 2 - 1;

function render(seconds, fn) {
  const out = new Float32Array(Math.floor(RATE * seconds));
  for (let i = 0; i < out.length; i++) out[i] = fn(i / RATE, i);
  return out;
}

// "Thwip": a burst of noise with a falling pitch sweep, like a web line firing.
function thwip() {
  let phase = 0, lp = 0;
  return render(0.22, (t) => {
    const f = 2600 * Math.pow(400 / 2600, t / 0.22);
    phase += (2 * Math.PI * f) / RATE;
    lp += Math.min(1, (2 * Math.PI * f) / RATE) * (noise() - lp);
    const env = Math.min(1, t / 0.004) * Math.exp(-t * 18);
    return env * (0.6 * lp + 0.35 * Math.sin(phase));
  });
}

// Spider-sense: quick shimmering high pings.
function tingle() {
  const pings = [[0, 2093], [0.07, 2637], [0.14, 3136], [0.21, 2637]];
  return render(0.75, (t) => pings.reduce((sum, [start, f]) => {
    const dt = t - start;
    if (dt < 0) return sum;
    const shimmer = 1 + 0.35 * Math.sin(2 * Math.PI * 28 * dt);
    return sum + Math.exp(-dt * 9) * Math.sin(2 * Math.PI * f * dt) * shimmer;
  }, 0));
}

// Task done: a friendly two-note chime.
function done() {
  const notes = [[0, 880], [0.12, 1318.5]];
  return render(0.9, (t) => notes.reduce((sum, [start, f]) => {
    const dt = t - start;
    if (dt < 0) return sum;
    const tone = Math.sin(2 * Math.PI * f * dt) + 0.3 * Math.sin(4 * Math.PI * f * dt);
    return sum + Math.min(1, dt / 0.005) * Math.exp(-dt * 6) * tone;
  }, 0));
}

function wav(samples) {
  const peak = samples.reduce((m, s) => Math.max(m, Math.abs(s)), 0) || 1;
  const data = Buffer.alloc(samples.length * 2);
  samples.forEach((s, i) => data.writeInt16LE(Math.round((s / peak) * 0.9 * 32767), i * 2));
  const header = Buffer.alloc(44);
  header.write('RIFF', 0);
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVE', 8);
  header.write('fmt ', 12);
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20); // PCM
  header.writeUInt16LE(1, 22); // mono
  header.writeUInt32LE(RATE, 24);
  header.writeUInt32LE(RATE * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36);
  header.writeUInt32LE(data.length, 40);
  return Buffer.concat([header, data]);
}

const assets = path.join(__dirname, '..', 'assets');
fs.mkdirSync(assets, { recursive: true });
for (const [name, fn] of Object.entries({ thwip, tingle, done })) {
  fs.writeFileSync(path.join(assets, `${name}.wav`), wav(fn()));
  console.log(`assets/${name}.wav`);
}
