// Alex Vasilcoiu (aav9060@rit.edu)
// CSCI 251 - Secure Distributed Messenger
//
// SPRINT 1: Threading & Basic Networking
// Due: Week 5 | Work on: Weeks 3-4
//
// KEY CONCEPTS USED IN THIS FILE:
//   - TcpClient: initiates outgoing connections (see HINTS.md)
//   - async/await: ConnectAsync, ReadAsync, WriteAsync
//   - NetworkStream: read/write bytes over network
//   - Length-prefix framing: 4-byte length + JSON payload
//
// CLIENT vs SERVER:
//   - Server (Server.cs) waits for others to connect TO it
//   - Client (this file) connects TO other servers
//   - Test: Terminal 1 runs /listen, Terminal 2 runs /connect
//
// SPRINT PROGRESSION:
//   - Sprint 1: Basic client for outgoing connections (this file)
//   - Sprint 2: Add encryption to message sending/receiving
//   - Sprint 3: Refactor to track connections as Peer objects,
//               integrate with PeerDiscovery for automatic connections
//

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SecureMessenger.Core;
using SecureMessenger.Security;

namespace SecureMessenger.Network;

/// <summary>
/// TCP client that connects to a server and handles message sending/receiving.
///
/// In Sprint 1-2, this handles a single outgoing connection.
///
/// In Sprint 3, connections are upgraded to "peers" with:
/// - Richer state tracking (see Peer.cs)
/// - Automatic reconnection on disconnect
/// - Integration with PeerDiscovery
/// </summary>
public class Client
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cancellationTokenSource;
    private string _serverEndpoint = "";

    // Sprint 2: Security components
    private KeyExchange? _keyExchange;
    private AesEncryption? _aesEncryption;
    private MessageSigner? _messageSigner;
    private RsaEncryption? _rsaEncryption;

    public event Action<string>? OnConnected;
    public event Action<string>? OnDisconnected;
    public event Action<Message>? OnMessageReceived;

    public bool IsConnected => _client?.Connected ?? false;

    /// <summary>
    /// Connect to a server at the specified address and port.
    ///
    /// TODO: Implement the following:
    /// 1. Create a new CancellationTokenSource
    /// 2. Create a new TcpClient
    /// 3. Connect asynchronously using await _client.ConnectAsync(host, port)
    /// 4. Get the NetworkStream from the client
    /// 5. Store the endpoint string (e.g., "192.168.1.5:5000")
    /// 6. Invoke OnConnected event
    /// 7. Start ReceiveAsync on a background Task
    /// 8. Return true on success
    /// 9. Catch exceptions, log error, and return false
    ///
    /// Sprint 3: This will be enhanced to create a Peer object and
    /// register it with the connection manager for reconnection support.
    /// </summary>
    public async Task<bool> ConnectAsync(string host, int port)
    {
        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _client = new TcpClient();
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();
            _serverEndpoint = $"{host}:{port}";

            // Sprint 2: Perform key exchange before starting receive loop
            await PerformKeyExchangeAsync();

            OnConnected?.Invoke(_serverEndpoint);
            _ = Task.Run(ReceiveAsync);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Client] ConnectAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Receive loop - runs on background thread.
    /// Uses length-prefix framing: 4 bytes for length, then JSON payload.
    ///
    /// TODO: Implement the following:
    /// 1. Create a 4-byte buffer for reading message length
    /// 2. Loop while not cancelled and client is connected:
    ///    a. Read 4 bytes for the message length
    ///    b. If bytesRead == 0, server disconnected - break
    ///    c. Convert bytes to int using BitConverter.ToInt32
    ///    d. Validate length (> 0 and < 1,000,000)
    ///    e. Create a buffer for the message payload
    ///    f. Read the full payload (may require multiple reads)
    ///    g. Convert to string using Encoding.UTF8.GetString
    ///    h. Deserialize JSON to Message using JsonSerializer.Deserialize
    ///    i. Invoke OnMessageReceived event
    /// 3. Catch OperationCanceledException (normal shutdown)
    /// 4. Catch other exceptions and log them
    /// 5. In finally block, invoke OnDisconnected event
    ///
    /// Sprint 3: Will be enhanced to update Peer.LastSeen and
    /// trigger reconnection attempts on unexpected disconnect.
    /// </summary>
    private async Task ReceiveAsync()
    {
        var lengthBuffer = new byte[4];
        try
        {
            while (!_cancellationTokenSource!.Token.IsCancellationRequested && (_client?.Connected ?? false))
            {
                int bytesRead = await _stream!.ReadAsync(lengthBuffer, 0, 4, _cancellationTokenSource.Token);

                if (bytesRead == 0)
                {
                    break;
                }

                int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                if (messageLength <= 0 || messageLength >= 1_000_000)
                {
                    Console.Error.WriteLine($"[Client] Invalid message length: {messageLength}");
                    break;
                }

                var payloadBuffer = new byte[messageLength];
                int totalRead = 0;

                while (totalRead < messageLength)
                {
                    int read = await _stream.ReadAsync(
                        payloadBuffer, totalRead, messageLength - totalRead,
                        _cancellationTokenSource.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    totalRead += read;
                }

                string json = Encoding.UTF8.GetString(payloadBuffer, 0, totalRead);
                var message = JsonSerializer.Deserialize<Message>(json);
                if (message != null)
                {
                    // Sprint 2: Decrypt and verify incoming messages
                    if (message.Type == MessageType.Text && _aesEncryption != null)
                    {
                        if (message.EncryptedContent != null)
                        {
                            message.Content = _aesEncryption.Decrypt(message.EncryptedContent);
                            message.EncryptedContent = null;
                        }
                    }
                    OnMessageReceived?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Client] ReceiveAsync error: {ex.Message}");
        }
        finally
        {
            OnDisconnected?.Invoke(_serverEndpoint);
        }
    }

    /// <summary>
    /// Send a message to the server.
    ///
    /// TODO: Implement the following:
    /// 1. Check if connected - if not, log error and return
    /// 2. Serialize the message to JSON using JsonSerializer.Serialize
    /// 3. Convert to bytes using Encoding.UTF8.GetBytes
    /// 4. Create a 4-byte length prefix using BitConverter.GetBytes
    /// 5. Write the length prefix to the stream
    /// 6. Write the payload to the stream
    /// 7. Handle exceptions
    ///
    /// Sprint 2: Add encryption before serialization
    /// Sprint 3: Will send to Peer instead of raw stream
    /// </summary>
    public void Send(Message message)
    {
        if (!IsConnected || _stream == null)
        {
            Console.Error.WriteLine("[Client] Send failed: not connected.");
            return;
        }
        try
        {
            // Sprint 2: Encrypt content before sending
            if (_aesEncryption != null && message.Type == MessageType.Text)
            {
                message.EncryptedContent = _aesEncryption.Encrypt(message.Content);
                message.Content = "[encrypted]";
            }

            string json = JsonSerializer.Serialize(message);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = BitConverter.GetBytes(payload.Length);
            _stream.Write(lengthPrefix, 0, lengthPrefix.Length);
            _stream.Write(payload, 0, payload.Length);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Client] Send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Disconnect from the server.
    ///
    /// TODO: Implement the following:
    /// 1. Cancel the cancellation token
    /// 2. Close the stream
    /// 3. Close the client
    /// </summary>
    public void Disconnect()
    {
        _cancellationTokenSource?.Cancel();
        _stream?.Close();
        _client?.Close();
        _rsaEncryption?.Dispose();
    }

    /// <summary>
    /// Sprint 2: Perform key exchange as initiator.
    /// 1. Send our public key
    /// 2. Receive peer's public key
    /// 3. Generate and send encrypted session key
    /// 4. Establish AES encryption with shared session key
    /// </summary>
    private async Task PerformKeyExchangeAsync()
    {
        _keyExchange = new KeyExchange();
        _rsaEncryption = new RsaEncryption();
        _messageSigner = new MessageSigner(System.Security.Cryptography.RSA.Create());

        // Step 1: Send our public key
        byte[] ourPublicKey = _keyExchange.GetPublicKey();
        var keyMsg = new Message
        {
            Type = MessageType.KeyExchange,
            PublicKey = ourPublicKey,
            Sender = "KeyExchange"
        };
        SendRaw(keyMsg);

        // Step 2: Receive peer's public key
        var peerKeyMsg = await ReceiveRawAsync();
        if (peerKeyMsg?.Type == MessageType.KeyExchange && peerKeyMsg.PublicKey != null)
        {
            _keyExchange.ReceivePublicKey(peerKeyMsg.PublicKey);
        }

        // Step 3: Generate session key, encrypt with peer's public key, send it
        byte[] encryptedSessionKey = _keyExchange.CreateEncryptedSessionKey();
        var sessionMsg = new Message
        {
            Type = MessageType.SessionKey,
            EncryptedContent = encryptedSessionKey,
            Sender = "KeyExchange"
        };
        SendRaw(sessionMsg);
        _keyExchange.Complete();

        // Step 4: Create AES encryption with the shared session key
        if (_keyExchange.SessionKey != null)
        {
            _aesEncryption = new AesEncryption(_keyExchange.SessionKey);
            Console.WriteLine("[security] Encrypted session established.");
        }
    }

    private void SendRaw(Message message)
    {
        if (_stream == null) return;
        string json = JsonSerializer.Serialize(message);
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] lengthPrefix = BitConverter.GetBytes(payload.Length);
        _stream.Write(lengthPrefix, 0, lengthPrefix.Length);
        _stream.Write(payload, 0, payload.Length);
    }

    private async Task<Message?> ReceiveRawAsync()
    {
        if (_stream == null) return null;
        var lengthBuffer = new byte[4];
        int bytesRead = await _stream.ReadAsync(lengthBuffer, 0, 4);
        if (bytesRead == 0) return null;

        int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
        if (messageLength <= 0 || messageLength >= 1_000_000) return null;

        var payloadBuffer = new byte[messageLength];
        int totalRead = 0;
        while (totalRead < messageLength)
        {
            int read = await _stream.ReadAsync(payloadBuffer, totalRead, messageLength - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        string json = Encoding.UTF8.GetString(payloadBuffer, 0, totalRead);
        return JsonSerializer.Deserialize<Message>(json);
    }
}
