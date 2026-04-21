using System.Security.Cryptography;
using System.Text;
using Sodium;

namespace Fluentia.Services;

public class CryptoService
{
    private KeyPair _keyPair;
    private byte[]? _peerPublicKey;

    // Receive ratchet (mobile → PC)
    private byte[]? _recvChainKey;
    private int _expectedSeq;
    private bool _ratchetReady;

    // Send ratchet (PC → mobile) — backward security
    private byte[]? _sendChainKey;
    private int _sendSeq;
    private bool _sendRatchetReady;

    public CryptoService()
    {
        _keyPair = PublicKeyBox.GenerateKeyPair();
    }

    public string PublicKeyBase64 => Convert.ToBase64String(_keyPair.PublicKey);
    public bool IsReady => _peerPublicKey != null;
    public bool RatchetReady => _ratchetReady;
    public bool SendRatchetReady => _sendRatchetReady;

    public void SetPeerPublicKey(string base64Key)
    {
        _peerPublicKey = Convert.FromBase64String(base64Key);
    }

    public void ResetPeerState()
    {
        _peerPublicKey = null;
        _recvChainKey = null;
        _expectedSeq = 0;
        _ratchetReady = false;
        _sendChainKey = null;
        _sendSeq = 0;
        _sendRatchetReady = false;
    }

    /// <summary>
    /// Initialize the receive ratchet from the seed sent by mobile.
    /// </summary>
    public void InitRatchet(string seedBase64)
    {
        var seed = Convert.FromBase64String(seedBase64);
        _recvChainKey = Kdf(seed, "fluentia_chain_v1");
        _expectedSeq = 0;
        _ratchetReady = true;
    }

    /// <summary>
    /// Initialize the PC's send ratchet for backward security.
    /// Returns the seed that must be sent to mobile.
    /// </summary>
    public string InitSendRatchet()
    {
        var seed = new byte[32];
        RandomNumberGenerator.Fill(seed);
        _sendChainKey = Kdf(seed, "fluentia_chain_v1");
        _sendSeq = 0;
        _sendRatchetReady = true;
        return Convert.ToBase64String(seed);
    }

    /// <summary>
    /// Ratcheted encryption (PC → mobile) using SecretBox.
    /// </summary>
    public (string Payload, string Nonce, int Seq) EncryptRatcheted(string plaintext)
    {
        if (_sendChainKey == null)
            throw new InvalidOperationException("Send ratchet not initialized");

        var (msgKey, nextChain) = RatchetStep(_sendChainKey);
        _sendChainKey = nextChain;
        var seq = _sendSeq++;

        var nonce = SecretBox.GenerateNonce();
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = SecretBox.Create(messageBytes, nonce, msgKey);

        Array.Clear(msgKey, 0, msgKey.Length);

        return (Convert.ToBase64String(encrypted), Convert.ToBase64String(nonce), seq);
    }

    /// <summary>
    /// Legacy crypto_box encryption.
    /// </summary>
    public (string Payload, string Nonce) Encrypt(string plaintext)
    {
        if (_peerPublicKey == null)
            throw new InvalidOperationException("Peer public key not set");

        var nonce = PublicKeyBox.GenerateNonce();
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = PublicKeyBox.Create(messageBytes, nonce, _keyPair.PrivateKey, _peerPublicKey);

        return (Convert.ToBase64String(encrypted), Convert.ToBase64String(nonce));
    }

    /// <summary>
    /// Legacy crypto_box decryption.
    /// </summary>
    public string Decrypt(string payloadBase64, string nonceBase64)
    {
        if (_peerPublicKey == null)
            throw new InvalidOperationException("Peer public key not set");

        var decrypted = PublicKeyBox.Open(
            Convert.FromBase64String(payloadBase64),
            Convert.FromBase64String(nonceBase64),
            _keyPair.PrivateKey,
            _peerPublicKey);

        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Ratcheted decryption using SecretBox with forward secrecy.
    /// </summary>
    public string DecryptRatcheted(string payloadBase64, string nonceBase64, int seq)
    {
        if (_recvChainKey == null)
            throw new InvalidOperationException("Ratchet not initialized");

        while (_expectedSeq < seq)
        {
            var (_, next) = RatchetStep(_recvChainKey);
            _recvChainKey = next;
            _expectedSeq++;
        }

        var (msgKey, nextChain) = RatchetStep(_recvChainKey);
        _recvChainKey = nextChain;
        _expectedSeq++;

        var decrypted = SecretBox.Open(
            Convert.FromBase64String(payloadBase64),
            Convert.FromBase64String(nonceBase64),
            msgKey);

        Array.Clear(msgKey, 0, msgKey.Length);

        return Encoding.UTF8.GetString(decrypted);
    }

    public void Reset()
    {
        _keyPair = PublicKeyBox.GenerateKeyPair();
        ResetPeerState();
    }

    private static byte[] Kdf(byte[] key, string label)
    {
        var labelBytes = Encoding.UTF8.GetBytes(label);
        var input = new byte[key.Length + labelBytes.Length];
        Buffer.BlockCopy(key, 0, input, 0, key.Length);
        Buffer.BlockCopy(labelBytes, 0, input, key.Length, labelBytes.Length);
        var hash = SHA512.HashData(input);
        return hash[..32];
    }

    private static (byte[] MessageKey, byte[] NextChainKey) RatchetStep(byte[] chainKey)
    {
        return (Kdf(chainKey, "msg"), Kdf(chainKey, "chain"));
    }
}
