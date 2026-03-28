// [Your Name Here]
// CSCI 251 - Secure Distributed Messenger
//
// SPRINT 1: Threading & Basic Networking
// Due: Week 5 | Work on: Weeks 3-4
//
// KEY CONCEPTS USED IN THIS FILE:
//   - TcpListener: accepts incoming connections (see HINTS.md)
//   - Threads/Tasks: accept loop runs on background thread
//   - Events (Action<T>): notify Program.cs when things happen
//   - Locking: protect _clients list from concurrent access
//
// SPRINT PROGRESSION:
//   - Sprint 1: Basic server with client connections (this file)
//   - Sprint 2: Add encryption to message sending/receiving
//   - Sprint 3: Refactor to use Peer class for richer connection tracking,
//               add heartbeat monitoring and reconnection support
//

using System.Net;
using System.Net.Sockets;
using SecureMessenger.Core;
using SecureMessenger.Security;

namespace SecureMessenger.Network;

/// <summary>
/// TCP server that listens for incoming connections.
///
/// In Sprint 1-2, we use simple client/server terminology:
/// - Server listens for incoming connections
/// - Connected parties are tracked as "clients"
///
/// In Sprint 3, this evolves to peer-to-peer:
/// - Connections become "peers" with richer state (see Peer.cs)
/// - Add peer discovery, heartbeats, and reconnection
/// </summary>
public class Server
{
    private TcpListener? _listener;
    private readonly List<TcpClient> _clients = new();
    private readonly object _clientsLock = new();
    private CancellationTokenSource? _cancellationTokenSource;

    // Sprint 2: Per-client security state
    private readonly Dictionary<TcpClient, AesEncryption> _clientEncryption = new();
    private readonly Dictionary<TcpClient, byte[]> _clientSigningKeys = new();
    private System.Security.Cryptography.RSA? _serverSigningKey;
    private MessageSigner? _serverSigner;
    private readonly object _encryptionLock = new();

    // Sprint 2: Chat rooms
    private readonly Dictionary<string, HashSet<TcpClient>> _rooms = new();
    private readonly object _roomsLock = new();

    // Events: invoke these with OnXxx?.Invoke(...) when something happens
    // Program.cs subscribes with: server.OnXxx += (args) => { ... };
    public event Action<string>? OnClientConnected;      // endpoint string, e.g. "192.168.1.5:54321"
    public event Action<string>? OnClientDisconnected;
    public event Action<Message>? OnMessageReceived;

    public int Port { get; private set; }
    public bool IsListening { get; private set; }

    /// <summary>
    /// Start listening for incoming connections on the specified port.
    ///
    /// TODO: Implement the following:
    /// 1. Store the port number in the Port property
    /// 2. Create a new CancellationTokenSource
    /// 3. Create a TcpListener on IPAddress.Any and the specified port
    /// 4. Call Start() on the listener
    /// 5. Set IsListening to true
    /// 6. Start AcceptClientsAsync on a background Task
    /// 7. Print a message indicating the server is listening
    /// </summary>
    public void Start(int port)
    {
        Port = port;
        _cancellationTokenSource = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        IsListening = true;

        // Sprint 2: Create server signing key
        _serverSigningKey = System.Security.Cryptography.RSA.Create(2048);
        _serverSigner = new MessageSigner(_serverSigningKey);

        Task.Run(() => AcceptClientsAsync());
        Console.WriteLine($"Listening on port: {port}. Waiting for connections...");
    }

    /// <summary>
    /// Main loop that accepts incoming connections.
    ///
    /// TODO: Implement the following:
    /// 1. Loop while cancellation is not requested
    /// 2. Use await _listener.AcceptTcpClientAsync(_cancellationTokenSource.Token)
    /// 3. Get the endpoint string from client.Client.RemoteEndPoint
    /// 4. Add the client to _clients (with proper locking)
    /// 5. Invoke OnClientConnected event with the endpoint
    /// 6. Start ReceiveFromClientAsync for this client on a background Task
    /// 7. Catch OperationCanceledException (normal shutdown - just break)
    /// 8. Catch other exceptions and log them
    /// </summary>
    private async Task AcceptClientsAsync()
    {
        try
        {
            while (!_cancellationTokenSource?.Token.IsCancellationRequested ?? false)
            {
                if (_listener == null || _cancellationTokenSource == null) break;
                TcpClient client = await _listener.AcceptTcpClientAsync(_cancellationTokenSource.Token);
                string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

                lock (_clientsLock)
                {
                    _clients.Add(client);
                }

                OnClientConnected?.Invoke(endpoint);

                var (aes, peerSigningKey) = await PerformKeyExchangeAsync(client);
                if (aes != null)
                {
                    lock (_encryptionLock)
                    {
                        _clientEncryption[client] = aes;
                        if (peerSigningKey != null)
                            _clientSigningKeys[client] = peerSigningKey;
                    }
                    Console.WriteLine($"[security] Encrypted session established with {endpoint}");
                }

                _ = Task.Run(() => ReceiveFromClientAsync(client, endpoint));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error accepting clients: {ex.Message}");
        }
    }

    /// <summary>
    /// Receive loop for a specific client - reads messages until disconnection.
    ///
    /// TODO: Implement the following:
    /// 1. Get the NetworkStream from the client
    /// 2. Create a 4-byte buffer for reading message length
    /// 3. Loop while not cancelled and client is connected:
    ///    a. Read 4 bytes for the message length (length-prefix framing)
    ///    b. If bytesRead == 0, client disconnected - break
    ///    c. Convert bytes to int using BitConverter.ToInt32
    ///    d. Validate length (> 0 and < 1,000,000)
    ///    e. Create a buffer for the message payload
    ///    f. Read the full payload (may require multiple reads)
    ///    g. Convert to string using Encoding.UTF8.GetString
    ///    h. Deserialize JSON to Message using JsonSerializer.Deserialize
    ///    i. Invoke OnMessageReceived event
    /// 4. Catch OperationCanceledException (normal shutdown)
    /// 5. Catch other exceptions and log them
    /// 6. In finally block, call DisconnectClient
    ///
    /// Sprint 3: This method will be enhanced to work with Peer objects
    /// instead of raw TcpClient, enabling richer connection state tracking.
    /// </summary>
    private async Task ReceiveFromClientAsync(TcpClient client, string endpoint)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            byte[] lengthBuffer = new byte[4];

            while (!_cancellationTokenSource?.Token.IsCancellationRequested ?? false)
            {
                // Read 4 bytes for message length
                int bytesRead = await stream.ReadAsync(lengthBuffer, 0, 4, _cancellationTokenSource?.Token ?? CancellationToken.None);
                if (bytesRead == 0) break; // Client disconnected

                int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (messageLength <= 0 || messageLength > 1_000_000)
                {
                    Console.WriteLine($"Invalid message length: {messageLength} from {endpoint}");
                    break;
                }

                byte[] payloadBuffer = new byte[messageLength];
                int totalBytesRead = 0;
                while (totalBytesRead < messageLength)
                {
                    int read = await stream.ReadAsync(payloadBuffer, totalBytesRead, messageLength - totalBytesRead, _cancellationTokenSource?.Token ?? CancellationToken.None);
                    if (read == 0) break; // Client disconnected
                    totalBytesRead += read;
                }

                string jsonString = System.Text.Encoding.UTF8.GetString(payloadBuffer);
                Message? message = System.Text.Json.JsonSerializer.Deserialize<Message>(jsonString);
                if (message != null)
                {
                    // Sprint 2: Decrypt incoming messages
                    AesEncryption? aes = null;
                    byte[]? peerSignKey = null;
                    lock (_encryptionLock)
                    {
                        _clientEncryption.TryGetValue(client, out aes);
                        _clientSigningKeys.TryGetValue(client, out peerSignKey);
                    }

                    if (aes != null && message.Type == MessageType.Text && message.EncryptedContent != null)
                    {
                        // Verify signature before decrypting
                        if (message.Signature != null && peerSignKey != null)
                        {
                            var verifier = new MessageSigner(System.Security.Cryptography.RSA.Create());
                            bool valid = verifier.VerifyData(message.EncryptedContent, message.Signature, peerSignKey);
                            if (!valid)
                            {
                                Console.WriteLine($"[security] Rejecting message from {endpoint} - invalid signature!");
                                continue;
                            }
                        }

                        message.Content = aes.Decrypt(message.EncryptedContent);
                        message.EncryptedContent = null;
                        message.Signature = null;
                    }

                    OnMessageReceived?.Invoke(message);

                    // Sprint 2: Route to room if specified, otherwise broadcast
                    if (!string.IsNullOrEmpty(message.Room))
                    {
                        SendToRoom(message.Room, message, client);
                    }
                    else
                    {
                        Broadcast(message, client);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (IOException)
        {
            Console.WriteLine($"Connection lost with {endpoint}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error receiving from client {endpoint}: {ex.Message}");
        }
        finally
        {
            DisconnectClient(client, endpoint);
        }
    }

    /// <summary>
    /// Clean up a disconnected client.
    ///
    /// TODO: Implement the following:
    /// 1. Remove the client from _clients (with proper locking)
    /// 2. Close the client connection
    /// 3. Invoke OnClientDisconnected event
    ///
    /// Sprint 3: This will be refactored to DisconnectPeer(Peer peer)
    /// to handle richer peer state and trigger reconnection attempts.
    /// </summary>
    private void DisconnectClient(TcpClient client, string endpoint)
    {
        lock (_clientsLock)
        {
            _clients.Remove(client);
        }
        lock (_encryptionLock)
        {
            _clientEncryption.Remove(client);
            _clientSigningKeys.Remove(client);
        }
        try
        {
            client.Close();
        }
        catch (Exception ex)
        {
            // do nothing, we're disconnecting anyway
        }
        OnClientDisconnected?.Invoke(endpoint);
    }

    /// <summary>
    /// Send a message to all connected clients (broadcast).
    ///
    /// TODO: Implement the following:
    /// 1. Serialize the message to JSON using JsonSerializer.Serialize
    /// 2. Convert to bytes using Encoding.UTF8.GetBytes
    /// 3. Create a 4-byte length prefix using BitConverter.GetBytes
    /// 4. Get a copy of _clients (with proper locking)
    /// 5. For each connected client:
    ///    a. Get the NetworkStream
    ///    b. Write the length prefix (4 bytes)
    ///    c. Write the payload
    /// 6. Handle exceptions for individual clients (don't stop broadcast)
    /// </summary>
    public void Broadcast(Message message)
    {
        List<TcpClient> clientsCopy;
        lock (_clientsLock)
        {
            clientsCopy = new List<TcpClient>(_clients);
        }

        foreach (var client in clientsCopy)
        {
            SendToClient(client, message);
        }
    }

    public void Broadcast(Message message, TcpClient? excludeClient = null)
    {
        if (excludeClient == null)
        {
            Broadcast(message);
            return;
        }

        List<TcpClient> clientsCopy;
        lock (_clientsLock)
        {
            clientsCopy = new List<TcpClient>(_clients);
        }

        foreach (var client in clientsCopy)
        {
            if (client == excludeClient) continue;
            SendToClient(client, message);
        }
    }

    /// <summary>
    /// Sprint 2: Send to a specific room. Only clients who are members receive the message.
    /// </summary>
    public void SendToRoom(string room, Message message, TcpClient? excludeClient = null)
    {
        List<TcpClient> members;
        lock (_roomsLock)
        {
            if (!_rooms.ContainsKey(room)) return;
            members = new List<TcpClient>(_rooms[room]);
        }

        foreach (var client in members)
        {
            if (client == excludeClient) continue;
            SendToClient(client, message);
        }
    }

    private void SendToClient(TcpClient client, Message message)
    {
        try
        {
            if (client.Connected)
            {
                var msgCopy = new Message
                {
                    Id = message.Id,
                    Sender = message.Sender,
                    Content = message.Content,
                    Timestamp = message.Timestamp,
                    Type = message.Type,
                    Room = message.Room
                };
                AesEncryption? aes = null;
                lock (_encryptionLock)
                {
                    _clientEncryption.TryGetValue(client, out aes);
                }
                if (aes != null && msgCopy.Type == MessageType.Text)
                {
                    msgCopy.EncryptedContent = aes.Encrypt(msgCopy.Content);
                    msgCopy.Content = "[encrypted]";

                    // Sign the encrypted content
                    if (_serverSigner != null)
                    {
                        msgCopy.Signature = _serverSigner.SignData(msgCopy.EncryptedContent);
                    }
                }

                string jsonString = System.Text.Json.JsonSerializer.Serialize(msgCopy);
                byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
                byte[] lengthPrefix = BitConverter.GetBytes(payloadBytes.Length);
                NetworkStream stream = client.GetStream();
                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                stream.Write(payloadBytes, 0, payloadBytes.Length);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending to client {client.Client.RemoteEndPoint}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop the server and close all connections.
    ///
    /// TODO: Implement the following:
    /// 1. Cancel the cancellation token
    /// 2. Stop the listener
    /// 3. Set IsListening to false
    /// 4. Close all clients (with proper locking)
    /// 5. Clear the _clients list
    /// </summary>
    public void Stop()
    {
        _cancellationTokenSource?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping listener: {ex.Message}");
        }

        IsListening = false;
        lock (_clientsLock)
        {
            foreach (TcpClient client in _clients)
            {
                try
                {
                    client.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error closing client {client.Client.RemoteEndPoint}: {ex.Message}");
                }
            }
            _clients.Clear();
        }
    }

    /// <summary>
    /// Get the count of currently connected clients.
    /// </summary>
    public int ClientCount
    {
        get
        {
            lock (_clientsLock)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>
    /// Sprint 2: Perform key exchange as responder.
    /// 1. Receive client's public key
    /// 2. Send our public key
    /// 3. Receive encrypted session key and decrypt it
    /// 4. Return AesEncryption with the shared session key
    /// </summary>
    private async Task<(AesEncryption?, byte[]?)> PerformKeyExchangeAsync(TcpClient client)
    {
        try
        {
            var keyExchange = new KeyExchange();
            NetworkStream stream = client.GetStream();

            // Step 1: Receive client's public key (encryption + signing)
            byte[]? peerSigningKey = null;
            var clientKeyMsg = await ReceiveRawAsync(stream);
            if (clientKeyMsg?.Type == MessageType.KeyExchange && clientKeyMsg.PublicKey != null)
            {
                keyExchange.ReceivePublicKey(clientKeyMsg.PublicKey);
                peerSigningKey = clientKeyMsg.Signature; // Client's signing public key
            }

            // Step 2: Send our public key (encryption + signing)
            byte[] ourPublicKey = keyExchange.GetPublicKey();
            var keyMsg = new Message
            {
                Type = MessageType.KeyExchange,
                PublicKey = ourPublicKey,
                Signature = _serverSigningKey?.ExportRSAPublicKey(),
                Sender = "KeyExchange"
            };
            SendRaw(stream, keyMsg);

            // Step 3: Receive encrypted session key
            var sessionMsg = await ReceiveRawAsync(stream);
            if (sessionMsg?.Type == MessageType.SessionKey && sessionMsg.EncryptedContent != null)
            {
                keyExchange.ReceiveEncryptedSessionKey(sessionMsg.EncryptedContent);
            }

            // Step 4: Create AES encryption with the shared session key
            if (keyExchange.SessionKey != null)
            {
                return (new AesEncryption(keyExchange.SessionKey), peerSigningKey);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[security] Key exchange failed: {ex.Message}");
        }
        return (null, null);
    }

    private void SendRaw(NetworkStream stream, Message message)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(message);
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] lengthPrefix = BitConverter.GetBytes(payload.Length);
        stream.Write(lengthPrefix, 0, lengthPrefix.Length);
        stream.Write(payload, 0, payload.Length);
    }

    private async Task<Message?> ReceiveRawAsync(NetworkStream stream)
    {
        var lengthBuffer = new byte[4];
        int bytesRead = await stream.ReadAsync(lengthBuffer, 0, 4);
        if (bytesRead == 0) return null;

        int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
        if (messageLength <= 0 || messageLength >= 1_000_000) return null;

        var payloadBuffer = new byte[messageLength];
        int totalRead = 0;
        while (totalRead < messageLength)
        {
            int read = await stream.ReadAsync(payloadBuffer, totalRead, messageLength - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        string json = System.Text.Encoding.UTF8.GetString(payloadBuffer, 0, totalRead);
        return System.Text.Json.JsonSerializer.Deserialize<Message>(json);
    }

    // Sprint 2: Chat room management

    public bool CreateRoom(string room)
    {
        lock (_roomsLock)
        {
            if (_rooms.ContainsKey(room)) return false;
            _rooms[room] = new HashSet<TcpClient>();
            return true;
        }
    }

    public bool JoinRoom(string room, TcpClient client)
    {
        lock (_roomsLock)
        {
            if (!_rooms.ContainsKey(room)) return false;
            return _rooms[room].Add(client);
        }
    }

    public bool LeaveRoom(string room, TcpClient client)
    {
        lock (_roomsLock)
        {
            if (!_rooms.ContainsKey(room)) return false;
            return _rooms[room].Remove(client);
        }
    }

    public List<string> GetRooms()
    {
        lock (_roomsLock)
        {
            return new List<string>(_rooms.Keys);
        }
    }

    /// <summary>
    /// Find the TcpClient by endpoint string (used by Program.cs to map client endpoint to TcpClient)
    /// </summary>
    public TcpClient? GetClientByEndpoint(string endpoint)
    {
        lock (_clientsLock)
        {
            return _clients.FirstOrDefault(c =>
                c.Client.RemoteEndPoint?.ToString() == endpoint);
        }
    }

    /// <summary>
    /// Get the first connected client (for single-client scenarios)
    /// </summary>
    public TcpClient? GetFirstClient()
    {
        lock (_clientsLock)
        {
            return _clients.FirstOrDefault();
        }
    }
}