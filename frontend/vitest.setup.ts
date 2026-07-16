import { randomUUID } from 'node:crypto'

// The app mints idempotency keys with crypto.randomUUID(). Browsers have it; jsdom versions
// differ on whether they expose it, and a component test failing on a missing polyfill rather
// than the behaviour under test would be pure noise.
if (!globalThis.crypto) {
  Object.defineProperty(globalThis, 'crypto', { value: {}, configurable: true })
}
if (typeof globalThis.crypto.randomUUID !== 'function') {
  Object.defineProperty(globalThis.crypto, 'randomUUID', { value: randomUUID, configurable: true })
}
