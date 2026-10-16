

'use strict';

let keyPromise = new Promise(() => {});

self.onmessage = (ev) => {
  const d = ev.data || {};
  if (d.type === 'key') {
    keyPromise = crypto.subtle.importKey('raw', d.key, 'AES-GCM', false, ['encrypt', 'decrypt']);
  } else if (d.type === 'stream') {

    pipe(d.readable, d.writable, d.op);
  }
};

self.onrtctransform = (ev) => {
  const t = ev.transformer;
  pipe(t.readable, t.writable, t.options.op);
};

function pipe(readable, writable, op) {
  const fn = op === 'encrypt' ? encrypt : decrypt;
  readable.pipeThrough(new TransformStream({ transform: fn })).pipeTo(writable).catch(() => {});
}

function clearBytes(frame) {
  const isVideo = (typeof RTCEncodedVideoFrame !== 'undefined') && (frame instanceof RTCEncodedVideoFrame);
  if (!isVideo) return 1;
  const mt = ((frame.getMetadata && frame.getMetadata().mimeType) || '').toLowerCase();
  if (mt.indexOf('vp8') >= 0) return frame.type === 'key' ? 10 : 3;
  if (mt.indexOf('vp9') >= 0) return 0;
  return 1;
}

async function encrypt(frame, controller) {
  const key = await keyPromise;
  const data = new Uint8Array(frame.data);

  if (frame.type === 'empty' || data.byteLength === 0) { controller.enqueue(frame); return; }
  const n = clearBytes(frame);
  if (data.byteLength <= n) { controller.enqueue(frame); return; }

  const header = data.subarray(0, n);
  const body = data.subarray(n);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await crypto.subtle.encrypt({ name: 'AES-GCM', iv, additionalData: header }, key, body));

  const out = new Uint8Array(n + ct.byteLength + 12);
  out.set(header, 0);
  out.set(ct, n);
  out.set(iv, n + ct.byteLength);
  frame.data = out.buffer;
  controller.enqueue(frame);
}

async function decrypt(frame, controller) {
  const key = await keyPromise;
  const data = new Uint8Array(frame.data);
  if (frame.type === 'empty' || data.byteLength === 0) { controller.enqueue(frame); return; }
  const n = clearBytes(frame);

  if (data.byteLength < n + 12 + 16) return;

  const header = data.subarray(0, n);
  const iv = data.subarray(data.byteLength - 12);
  const ct = data.subarray(n, data.byteLength - 12);
  try {
    const pt = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv, additionalData: header }, key, ct));
    const out = new Uint8Array(n + pt.byteLength);
    out.set(header, 0);
    out.set(pt, n);
    frame.data = out.buffer;
    controller.enqueue(frame);
  } catch {

  }
}
