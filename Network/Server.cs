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
    private readonly List<Peer> _peers = new();
    private readonly object _peersLock = new();
    private CancellationTokenSource? _cancellationTokenSource;

    // Sprint 2: Per-client security state
    private readonly Dictionary<Peer, AesEncryption> _peerEncryption = new();
    private readonly Dictionary<Peer, byte[]> _peerSigningKeys = new();
    private System.Security.Cryptography.RSA? _serverSigningKey;
    private MessageSigner? _serverSigner;
    private readonly object _encryptionLock = new();

    // Sprint 2: Chat rooms
    private readonly Dictionary<string, HashSet<Peer>> _rooms = new();
    private readonly object _roomsLock = new();

    // Debug mode: print wire messages before sending
    public bool DebugMode { get; set; } = false;

    // Events: invoke these with OnXxx?.Invoke(...) when something happens
    // Program.cs subscribes with: server.OnXxx += (args) => { ... };
    public event Action<Peer>? OnClientConnected;
    public event Action<Peer>? OnClientDisconnected;
    public event Action<Message>? OnMessageReceived;

    public int Port { get; private set; }
    public bool IsListening { get; private set; }

    public HeartbeatMonitor? HeartbeatMonitor { get; set; }

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
                Peer peer = CreatePeer(client, endpoint);

                lock (_peersLock)
                {
                    _peers.Add(peer);
                }

                OnClientConnected?.Invoke(peer);

                var (aes, peerSigningKey) = await PerformKeyExchangeAsync(peer);
                if (aes != null)
                {
                    lock (_encryptionLock)
                    {
                        _peerEncryption[peer] = aes;
                        if (peerSigningKey != null)
                            _peerSigningKeys[peer] = peerSigningKey;
                    }
                    Console.WriteLine($"[security] Encrypted session established with {endpoint}");
                }

                _ = Task.Run(() => ReceiveFromClientAsync(peer));
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
    private async Task ReceiveFromClientAsync(Peer peer)
    {
        string endpoint = peer.Client?.Client.RemoteEndPoint?.ToString() ?? $"{peer.Address}:{peer.Port}";

        try
        {
            if (peer.Client == null)
            {
                return;
            }
            NetworkStream stream = peer.Client.GetStream();
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
                    peer.LastSeen = DateTime.Now;
                    UpdatePeerIdentity(peer, message.Sender);

                    // Sprint 2: Decrypt incoming messages
                    AesEncryption? aes = null;
                    byte[]? peerSignKey = null;
                    lock (_encryptionLock)
                    {
                        _peerEncryption.TryGetValue(peer, out aes);
                        _peerSigningKeys.TryGetValue(peer, out peerSignKey);
                    }

                    if (message.Type == MessageType.Heartbeat)
                    {
                        HeartbeatMonitor?.RecordHeartbeat(message.Sender);
                        continue;
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

                    // Sprint 2: Handle room commands from clients
                    if (message.Type == MessageType.RoomCommand)
                    {
                        HandleRoomCommand(peer, message);
                    }
                    else
                    {
                        OnMessageReceived?.Invoke(message);

                        //// Sprint 2: Route to room if specified, otherwise broadcast
                        //if (!string.IsNullOrEmpty(message.Room))
                        //{
                        //    SendToRoom(message.Room, message, client);
                        //}
                        //else
                        //{
                        //    Broadcast(message, client);
                        //}
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
            DisconnectPeer(peer);
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
    private void DisconnectPeer(Peer peer)
    {
        lock (_peersLock)
        {
            _peers.Remove(peer);
        }
        lock (_encryptionLock)
        {
            _peerEncryption.Remove(peer);
            _peerSigningKeys.Remove(peer);
        }
        lock (_roomsLock)
        {
            foreach (var room in _rooms.Values)
                room.Remove(peer);
        }

        string endpoint = peer.Client?.Client.RemoteEndPoint?.ToString() ?? $"{peer.Address}:{peer.Port}";
        peer.Dispose();
        OnClientDisconnected?.Invoke(peer);
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
        // Never broadcast room-targeted chat messages — use SendToRoom instead.
        // RoomCommand is metadata and should be broadcast for room state sync.
        if (!string.IsNullOrEmpty(message.Room) && message.Type != MessageType.RoomCommand) return;

        List<Peer> peersCopy;
        lock (_peersLock)
        {
            peersCopy = new List<Peer>(_peers);
        }

        foreach (var peer in peersCopy)
        {
            SendToClient(peer, message);
        }
    }

    public void Broadcast(Message message, Peer? excludePeer = null)
    {
        // Never broadcast room-targeted chat messages — use SendToRoom instead.
        // RoomCommand is metadata and should be broadcast for room state sync.
        if (!string.IsNullOrEmpty(message.Room) && message.Type != MessageType.RoomCommand) return;

        if (excludePeer == null)
        {
            Broadcast(message);
            return;
        }

        List<Peer> peersCopy;
        lock (_peersLock)
        {
            peersCopy = new List<Peer>(_peers);
        }

        foreach (var peer in peersCopy)
        {
            if (peer == excludePeer) continue;
            SendToClient(peer, message);
        }
    }

    /// <summary>
    /// Sprint 2: Send to a specific room. Only clients who are members receive the message.
    /// </summary>
    public void SendToRoom(string room, Message message, Peer? excludePeer = null)
    {
        List<Peer> members;
        lock (_roomsLock)
        {
            if (!_rooms.ContainsKey(room)) return;
            members = new List<Peer>(_rooms[room]);
        }

        foreach (var peer in members)
        {
            if (peer == excludePeer) continue;
            SendToClient(peer, message);
        }
    }

    private void SendToClient(Peer peer, Message message)
    {
        try
        {
            if (peer.Client?.Connected == true)
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
                    _peerEncryption.TryGetValue(peer, out aes);
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

                if (DebugMode)
                {
                    string endpoint = peer.Client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                    Console.WriteLine($"[debug] -> {endpoint}: {jsonString}");
                }

                byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(jsonString);
                byte[] lengthPrefix = BitConverter.GetBytes(payloadBytes.Length);
                NetworkStream stream = peer.Client.GetStream();
                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                stream.Write(payloadBytes, 0, payloadBytes.Length);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending to client {peer.Client?.Client.RemoteEndPoint}: {ex.Message}");
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
        lock (_peersLock)
        {
            foreach (Peer peer in _peers)
            {
                try
                {
                    peer.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error closing client {peer.Client?.Client.RemoteEndPoint}: {ex.Message}");
                }
            }
            _peers.Clear();
        }
    }

    /// <summary>
    /// Get the count of currently connected clients.
    /// </summary>
    public int ClientCount
    {
        get
        {
            lock (_peersLock)
            {
                return _peers.Count;
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
    private async Task<(AesEncryption?, byte[]?)> PerformKeyExchangeAsync(Peer peer)
    {
        try
        {
            var keyExchange = new KeyExchange();
            if (peer.Client == null)
            {
                return (null, null);
            }

            NetworkStream stream = peer.Client.GetStream();

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
            _rooms[room] = new HashSet<Peer>();
            return true;
        }
    }

    public bool JoinRoom(string room, Peer? peer)
    {
        lock (_roomsLock)
        {
            if (!_rooms.ContainsKey(room)) return false;
            if (peer == null) return true; // Server operator - tracked in Program.cs
            return _rooms[room].Add(peer);
        }
    }

    public bool LeaveRoom(string room, Peer? peer)
    {
        lock (_roomsLock)
        {
            if (!_rooms.ContainsKey(room)) return false;
            if (peer == null) return true; // Server operator - tracked in Program.cs
            return _rooms[room].Remove(peer);
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
    /// Handle a room command message from a client and send a response back.
    /// </summary>
    private void HandleRoomCommand(Peer peer, Message message)
    {
        string command = message.Content; // e.g. "join", "create", "leave", "list"
        string room = message.Room ?? "";
        string response;

        switch (command)
        {
            case "create":
                bool created = CreateRoom(room);
                response = created ? $"Room {room} created." : $"Room {room} already exists.";
                break;
            case "join":
                if (string.IsNullOrEmpty(room))
                {
                    response = "Room name required.";
                }
                else
                {
                    bool joined = JoinRoom(room, peer);
                    response = joined ? $"Joined room {room}." : $"Could not join room {room} (doesn't exist or already joined).";
                }
                break;
            case "leave":
                bool left = LeaveRoom(room, peer);
                response = left ? $"Left room {room}." : $"Could not leave room {room}.";
                break;
            case "list":
                var rooms = GetRooms();
                response = rooms.Count == 0 ? "No rooms available." : "Available rooms:\n" + string.Join("\n", rooms.Select(r => $"  {r}"));
                break;
            default:
                response = $"Unknown room command: {command}";
                break;
        }

        //var responseMsg = new Message
        //{
        //    Sender = "Server",
        //    Content = response,
        //    Type = MessageType.Text
        //};
        //SendToClient(peer, responseMsg);
    }

    /// <summary>
    /// Find the TcpClient by endpoint string (used by Program.cs to map client endpoint to TcpClient)
    /// </summary>
    public TcpClient? GetClientByEndpoint(string endpoint)
    {
        lock (_peersLock)
        {
            return _peers.FirstOrDefault(p =>
                p.Client?.Client.RemoteEndPoint?.ToString() == endpoint)?.Client;
        }
    }

    public List<Peer> GetConnectedPeers()
    {
        lock (_peersLock)
        {
            return new List<Peer>(_peers);
        }
    }

    /// <summary>
    /// Get the first connected client (for single-client scenarios)
    /// </summary>
    public TcpClient? GetFirstClient()
    {
        lock (_peersLock)
        {
            return _peers.FirstOrDefault()?.Client;
        }
    }

    private Peer CreatePeer(TcpClient client, string endpoint)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;

        return new Peer
        {
            Id = endpoint,
            Name = endpoint,
            Address = remoteEndPoint?.Address,
            Port = remoteEndPoint?.Port ?? 0,
            Client = client,
            Stream = client.GetStream(),
            IsConnected = true,
            LastSeen = DateTime.Now
        };
    }

    private void UpdatePeerIdentity(Peer peer, string sender)
    {
        if (string.IsNullOrWhiteSpace(sender) || sender is "Server" or "KeyExchange") return;

        if (peer.Id != sender)
        {
            HeartbeatMonitor?.StopMonitoring(peer.Id);
            peer.Id = sender;
            HeartbeatMonitor?.StartMonitoring(peer.Id);
        }

        if (string.IsNullOrWhiteSpace(peer.Name) || peer.Name == $"{peer.Address}:{peer.Port}")
        {
            peer.Name = sender;
        }
    }
}