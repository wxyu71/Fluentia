using Sodium;

namespace Fluentia.Services;

public class CryptoService
{
    private KeyPair _keyPair;
    private byte[]? _peerPublicKey;

    public CryptoService()
    {
        _keyPair = PublicKeyBox.GenerateKeyPair();
    }

    public string PublicKeyBase64 => Convert.ToBase64String(_keyPair.PublicKey);

    public bool IsReady => _peerPublicKey != null;

    public void SetPeerPublicKey(string base64Key)
    {
        _peerPublicKey = Convert.FromBase64String(base64Key);
    }

    public (string Payload, string Nonce) Encrypt(string plaintext)
    {
        if (_peerPublicKey == null)
            throw new InvalidOperationException("Peer public key not set");

        var nonce = PublicKeyBox.GenerateNonce();
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var encrypted = PublicKeyBox.Create(messageBytes, nonce, _keyPair.PrivateKey, _peerPublicKey);

        return (Convert.ToBase64String(encrypted), Convert.ToBase64String(nonce));
    }

    public string Decrypt(string payloadBase64, string nonceBase64)
    {
        if (_peerPublicKey == null)
            throw new InvalidOperationException("Peer public key not set");

        var decrypted = PublicKeyBox.Open(
            Convert.FromBase64String(payloadBase64),
            Convert.FromBase64String(nonceBase64),
            _keyPair.PrivateKey,
            _peerPublicKey);

        return System.Text.Encoding.UTF8.GetString(decrypted);
    }

    public void Reset()
    {
        _keyPair = PublicKeyBox.GenerateKeyPair();
        _peerPublicKey = null;
    }
}
