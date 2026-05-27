/**
 * Generate cross-platform test vectors for CryptoService verification.
 * Uses tweetnacl (same library as the mobile PWA) to produce deterministic
 * outputs that Android's CryptoService must match byte-for-byte.
 *
 * Run: node scripts/generate-test-vectors.js
 * Output: android/app/src/test/resources/test-vectors/crypto-vectors.json
 */

const nacl = require('tweetnacl');
const fs = require('fs');
const path = require('path');

// Use a deterministic RNG for reproducible vectors
let rngState = 0x12345678;
function deterministicRandom(n) {
  const bytes = new Uint8Array(n);
  for (let i = 0; i < n; i++) {
    rngState = (rngState * 1103515245 + 12345) & 0x7fffffff;
    bytes[i] = rngState & 0xff;
  }
  return bytes;
}

// --- KDF test ---
function kdf(key, label) {
  const labelBytes = new TextEncoder().encode(label);
  const input = new Uint8Array(key.length + labelBytes.length);
  input.set(key);
  input.set(labelBytes, key.length);
  return nacl.hash(input).subarray(0, 32);
}

// Generate fixed keypairs using deterministic RNG
const aliceSeed = deterministicRandom(32);
const bobSeed = deterministicRandom(32);

// tweetnacl keypair from seed uses nacl.box.keyPair.fromSecretKey
const aliceKeyPair = nacl.box.keyPair.fromSecretKey(aliceSeed);
const bobKeyPair = nacl.box.keyPair.fromSecretKey(deterministicRandom(32));

// Actually, let's use proper keypair generation with fixed seed
// nacl.box.keyPair() uses crypto.getRandomValues, so we need to mock it
// Instead, let's generate keypairs from known seeds
function keypairFromSeed(seed) {
  // tweetnacl doesn't have fromSeed for box, but we can use the secret key directly
  // The public key is derived from the secret key via scalarmult base
  return nacl.box.keyPair.fromSecretKey(seed);
}

// Generate 32-byte seeds deterministically
const aliceSk = deterministicRandom(32);
const bobSk = deterministicRandom(32);

const alice = keypairFromSeed(aliceSk);
const bob = keypairFromSeed(bobSk);

// Ratchet seeds
const aliceRatchetSeed = deterministicRandom(32);
const bobRatchetSeed = deterministicRandom(32);

// KDF results
const aliceChainKey = kdf(aliceRatchetSeed, 'fluentia_chain_v1');
const bobChainKey = kdf(bobRatchetSeed, 'fluentia_chain_v1');

// Ratchet step function
function ratchetStep(chainKey) {
  return {
    messageKey: kdf(chainKey, 'msg'),
    nextChainKey: kdf(chainKey, 'chain'),
  };
}

// Generate ratcheted encryption vectors
function generateRatchetVectors(chainKey, count) {
  const vectors = [];
  let ck = chainKey;
  for (let i = 0; i < count; i++) {
    const { messageKey, nextChainKey } = ratchetStep(ck);
    const nonce = deterministicRandom(24);
    const plaintext = `message_${i}`;
    const messageBytes = new TextEncoder().encode(plaintext);
    const cipher = nacl.secretbox(messageBytes, nonce, messageKey);
    vectors.push({
      seq: i,
      plaintext,
      nonce: Buffer.from(nonce).toString('base64'),
      ciphertext: Buffer.from(cipher).toString('base64'),
    });
    // Wipe message key
    messageKey.fill(0);
    ck = nextChainKey;
  }
  return vectors;
}

const aliceSendVectors = generateRatchetVectors(aliceChainKey, 5);
const bobSendVectors = generateRatchetVectors(bobChainKey, 5);

// Box encrypt/decrypt vectors
const boxNonce = deterministicRandom(24);
const boxPlaintext = 'Hello, Fluentia!';
const boxMessageBytes = new TextEncoder().encode(boxPlaintext);
const boxCipher = nacl.box(boxMessageBytes, boxNonce, bob.publicKey, alice.secretKey);

// Decrypt with Bob's key
const boxDecrypted = nacl.box.open(boxCipher, boxNonce, alice.publicKey, bob.secretKey);
const boxDecryptedText = new TextDecoder().decode(boxDecrypted);

// KDF vectors
const kdfInput = deterministicRandom(32);
const kdfLabel = 'fluentia_chain_v1';
const kdfResult = kdf(kdfInput, kdfLabel);

// SHA-512 vector
const shaInput = new TextEncoder().encode('test');
const shaOutput = nacl.hash(shaInput);

// ScalarMult vector (for verification code derivation)
const scalarSk = aliceSk.subarray(0, 32);
const scalarPk = bob.publicKey;
const sharedSecret = nacl.scalarMult(scalarSk, scalarPk);

// Build the output
const vectors = {
  description: 'Fluentia cross-platform crypto test vectors generated from tweetnacl',
  protocolVersion: '1.7.0',
  alice: {
    secretKey: Buffer.from(alice.secretKey).toString('base64'),
    publicKey: Buffer.from(alice.publicKey).toString('base64'),
  },
  bob: {
    secretKey: Buffer.from(bob.secretKey).toString('base64'),
    publicKey: Buffer.from(bob.publicKey).toString('base64'),
  },
  kdf: {
    input: Buffer.from(kdfInput).toString('base64'),
    label: kdfLabel,
    output: Buffer.from(kdfResult).toString('base64'),
  },
  sha512: {
    input: 'dGVzdA==',  // base64("test")
    output: Buffer.from(shaOutput).toString('base64'),
  },
  box: {
    sender: 'alice',
    receiver: 'bob',
    plaintext: boxPlaintext,
    nonce: Buffer.from(boxNonce).toString('base64'),
    ciphertext: Buffer.from(boxCipher).toString('base64'),
    decrypted: boxDecryptedText,
  },
  ratchet: {
    aliceSeed: Buffer.from(aliceRatchetSeed).toString('base64'),
    aliceChainKey: Buffer.from(aliceChainKey).toString('base64'),
    bobSeed: Buffer.from(bobRatchetSeed).toString('base64'),
    bobChainKey: Buffer.from(bobChainKey).toString('base64'),
    aliceSends: aliceSendVectors,
    bobSends: bobSendVectors,
  },
  scalarMult: {
    secretKey: Buffer.from(scalarSk).toString('base64'),
    publicKey: Buffer.from(scalarPk).toString('base64'),
    sharedSecret: Buffer.from(sharedSecret).toString('base64'),
  },
};

// Write output
const outPath = path.join(__dirname, '..', '..', 'android', 'app', 'src', 'test', 'resources', 'test-vectors', 'crypto-vectors.json');
fs.writeFileSync(outPath, JSON.stringify(vectors, null, 2));
console.log('Test vectors written to:', outPath);
console.log('Alice PK:', vectors.alice.publicKey);
console.log('Bob PK:', vectors.bob.publicKey);
console.log('Box ciphertext:', vectors.box.ciphertext.substring(0, 40) + '...');
