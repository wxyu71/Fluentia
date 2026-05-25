using Fluentia.Services;

namespace Fluentia.Tests;

public class CryptoServiceTests
{
    [Fact]
    public void Constructor_GeneratesValidKeypair()
    {
        var svc = new CryptoService();
        Assert.NotNull(svc.PublicKeyBase64);
        Assert.NotNull(svc.PrivateKeyBase64);
        Assert.False(svc.IsReady);
    }

    [Fact]
    public void SetPeerPublicKey_MakesServiceReady()
    {
        var svc = new CryptoService();
        var peer = new CryptoService();
        svc.SetPeerPublicKey(peer.PublicKeyBase64);
        Assert.True(svc.IsReady);
    }

    [Fact]
    public void Encrypt_Decrypt_Roundtrip()
    {
        var alice = new CryptoService();
        var bob = new CryptoService();

        alice.SetPeerPublicKey(bob.PublicKeyBase64);
        bob.SetPeerPublicKey(alice.PublicKeyBase64);

        var plaintext = "Hello, Fluentia!";
        var (payload, nonce) = alice.Encrypt(plaintext);
        var decrypted = bob.Decrypt(payload, nonce);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_ThrowsWithoutPeerKey()
    {
        var svc = new CryptoService();
        Assert.Throws<InvalidOperationException>(() => svc.Encrypt("test"));
    }

    [Fact]
    public void Decrypt_ThrowsWithoutPeerKey()
    {
        var svc = new CryptoService();
        Assert.Throws<InvalidOperationException>(() => svc.Decrypt("x", "y"));
    }

    [Fact]
    public void EncryptRatcheted_DecryptRatcheted_Roundtrip()
    {
        var sender = new CryptoService();
        var receiver = new CryptoService();

        var seed = sender.InitSendRatchet();
        Assert.True(sender.SendRatchetReady);

        receiver.InitRatchet(seed);
        Assert.True(receiver.RatchetReady);

        for (int i = 0; i < 5; i++)
        {
            var msg = $"message {i}";
            var (payload, nonce, seq) = sender.EncryptRatcheted(msg);
            var decrypted = receiver.DecryptRatcheted(payload, nonce, seq);
            Assert.Equal(msg, decrypted);
        }
    }

    [Fact]
    public void EncryptRatcheted_ThrowsWithoutRatchet()
    {
        var svc = new CryptoService();
        Assert.Throws<InvalidOperationException>(() => svc.EncryptRatcheted("test"));
    }

    [Fact]
    public void DecryptRatcheted_ThrowsWithoutRatchet()
    {
        var svc = new CryptoService();
        Assert.Throws<InvalidOperationException>(() => svc.DecryptRatcheted("x", "y", 0));
    }

    [Fact]
    public void DecryptRatcheted_HandlesSkippedSeq()
    {
        var sender = new CryptoService();
        var receiver = new CryptoService();

        var seed = sender.InitSendRatchet();
        receiver.InitRatchet(seed);

        var msgs = new List<(string Payload, string Nonce, int Seq)>();
        for (int i = 0; i < 5; i++)
        {
            msgs.Add(sender.EncryptRatcheted($"msg {i}"));
        }

        var decrypted = receiver.DecryptRatcheted(msgs[4].Payload, msgs[4].Nonce, msgs[4].Seq);
        Assert.Equal("msg 4", decrypted);
    }

    [Fact]
    public void Reset_GeneratesNewKeypair()
    {
        var svc = new CryptoService();
        var oldPub = svc.PublicKeyBase64;
        svc.SetPeerPublicKey(Convert.ToBase64String(PublicKeyBox.GenerateKeyPair().PublicKey));
        svc.InitSendRatchet();

        svc.Reset();

        Assert.NotEqual(oldPub, svc.PublicKeyBase64);
        Assert.False(svc.IsReady);
        Assert.False(svc.SendRatchetReady);
    }

    [Fact]
    public void ImportKeyPair_LoadsCorrectly()
    {
        var original = new CryptoService();
        var pub = original.PublicKeyBase64;
        var priv = original.PrivateKeyBase64;

        var imported = new CryptoService();
        imported.ImportKeyPair(pub, priv);

        Assert.Equal(pub, imported.PublicKeyBase64);
        Assert.Equal(priv, imported.PrivateKeyBase64);
    }

    [Fact]
    public void KDF_ProducesDeterministicOutput()
    {
        var sender1 = new CryptoService();
        var seed1 = sender1.InitSendRatchet();

        var recv1 = new CryptoService();
        var recv2 = new CryptoService();
        recv1.InitRatchet(seed1);
        recv2.InitRatchet(seed1);

        var (payload, nonce, seq) = sender1.EncryptRatcheted("test");

        var dec1 = recv1.DecryptRatcheted(payload, nonce, seq);
        var dec2 = recv2.DecryptRatcheted(payload, nonce, seq);

        Assert.Equal("test", dec1);
        Assert.Equal("test", dec2);
    }

    [Fact]
    public void CreateBleVerificationCode_ProducesSixDigits()
    {
        var alice = new CryptoService();
        var bob = new CryptoService();

        var code = alice.CreateBleVerificationCode(bob.PublicKeyBase64);
        Assert.Matches(@"^\d{6}$", code);
    }

    [Fact]
    public void CreateBleVerificationCode_ConsistentForSameInputs()
    {
        var alice = new CryptoService();
        var bob = new CryptoService();

        var code1 = alice.CreateBleVerificationCode(bob.PublicKeyBase64);
        var code2 = alice.CreateBleVerificationCode(bob.PublicKeyBase64);

        Assert.Equal(code1, code2);
    }
}
