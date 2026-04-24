// [Your Name Here]
// CSCI 251 - Secure Distributed Messenger
// Group Project
//
// SPRINT 1: Threading & Basic Networking
// Due: Week 5 | Work on: Weeks 3-4
// (Continue enhancing in Sprints 2 & 3)
//

using System.Net;
using SecureMessenger.Core;
using SecureMessenger.Network;
using SecureMessenger.Security;
using SecureMessenger.UI;

namespace SecureMessenger;

/// <summary>
/// Main entry point for the Secure Distributed Messenger.
///
/// Architecture Overview:
/// This application uses multiple threads to handle concurrent operations:
///
/// 1. Main Thread (UI Thread)
///    - Reads user input from console
///    - Parses commands using ConsoleUI
///    - Dispatches commands to appropriate handlers
///
/// 2. Accept Thread (Server)
///    - Runs Server to accept incoming connections
///    - Each accepted connection spawns a receive task
///
/// 3. Receive Task(s)
///    - One per connected client
///    - Reads messages from network
///    - Invokes OnMessageReceived event
///
/// 4. Client Receive Task
///    - Reads messages from server we connected to
///    - Invokes OnMessageReceived event
///
/// Thread Communication:
/// - Use events for connection/disconnection/message notifications
/// - Use CancellationToken for graceful shutdown
/// - (Optional) Use MessageQueue for more complex processing pipelines
///
/// Sprint Progression:
/// - Sprint 1: Basic threading and networking (connect, send, receive)
///             Uses simple Client/Server model
/// - Sprint 2: Add encryption (key exchange, AES encryption, signing)
/// - Sprint 3: Upgrade to peer-to-peer model with Peer class,
///             add peer discovery, heartbeat, and reconnection
/// </summary>
class Program
{
    private static Server? _server;
    private static Client? _client;
    private static ConsoleUI? _ui;
    private static MessageQueue? _queue;
    private static CancellationTokenSource _cts;
    private static string _username = "User";
    private static string _clientEndpoint = "";
    private static readonly HashSet<string> _joinedRooms = new();
    private static string? _activeRoom;


    private static readonly List<Client> _outgoingPeers = new();
    private static PeerDiscovery _discovery = new();
    private static HeartbeatMonitor _heartbeat = new();
    private static MessageHistory _history = new();
    private static ReconnectionPolicy? _reconnect;
    private static string _localId = "";

    // TODO: Declare your components as fields for access across methods
    // Sprint 1-2 components:
    // private static Server? _server;
    // private static Client? _client;
    // private static ConsoleUI? _ui;
    // private static string _username = "User";
    //
    // Sprint 3 additions:
    // private static PeerDiscovery? _peerDiscovery;
    // private static HeartbeatMonitor? _heartbeatMonitor;

    static async Task Main(string[] args)
    {
        Console.WriteLine("Secure Distributed Messenger");
        Console.WriteLine("============================");
        _server = new Server();
        _client = new Client();
        _ui = new ConsoleUI();

        _queue = new MessageQueue();
        _cts = new CancellationTokenSource();
        // TODO: Initialize components
        // 1. Create Server for incoming connections
        // 2. Create Client for outgoing connection
        // 3. Create ConsoleUI for user interface
        // 4. (Optional) Create MessageQueue if using producer/consumer pattern

        Action<Message> handleMessage = (message) => {
            if (message.Type == MessageType.Heartbeat)
            {
                _heartbeat.RecordHeartbeat(message.Sender);
                return;
            }

            if (!string.IsNullOrEmpty(message.Room) && !_joinedRooms.Contains(message.Room))
            {
                return;
            }

            if (message.Type == MessageType.Text)
            {
                _history.SaveMessage(message);
            }

            _queue.EnqueueIncoming(message);
        };

        _server.HeartbeatMonitor = _heartbeat;
        _client.HeartbeatMonitor = _heartbeat;

        _server.OnMessageReceived += handleMessage;
        _client.OnMessageReceived += handleMessage;

        _server.OnClientConnected += endPoint => { Console.WriteLine($"[server] Client connected: {endPoint}"); };
        _server.OnClientDisconnected += endPoint => { Console.WriteLine($"[server] Client disconnected: {endPoint}"); };


        _client.OnConnected += endPoint => { _clientEndpoint = endPoint; Console.WriteLine($"[client] Connected to {endPoint}"); };
        _client.OnDisconnected += endPoint => { Console.WriteLine($"[client] Disconnected from {endPoint}"); };


        _localId = _discovery.LocalPeerId;
        _reconnect = new ReconnectionPolicy(_client);

        _discovery.OnPeerDiscovered += async (peer) => {
            if (string.Compare(_localId, peer.Id) > 0)
            {
                Console.WriteLine($"[Discovery] Found peer {peer.Id}. Connecting...");
                var newClient = new Client();

                newClient.HeartbeatMonitor = _heartbeat;

                newClient.OnMessageReceived += handleMessage;

                bool connected = await newClient.ConnectAsync(peer.Address.ToString(), peer.Port);
                if (connected)
                {
                    lock (_outgoingPeers) { _outgoingPeers.Add(newClient); }
                    _heartbeat.StartMonitoring(peer.Id);
                }
            }
        };
        // TODO: Subscribe to events
        // Server events:
        // - _server.OnClientConnected += endpoint => { ... };
        // - _server.OnClientDisconnected += endpoint => { ... };
        // - _server.OnMessageReceived += message => { ... };
        //
        // Client events:
        // - _client.OnConnected += endpoint => { ... };
        // - _client.OnDisconnected += endpoint => { ... };
        // - _client.OnMessageReceived += message => { ... };

        Console.WriteLine("Type /help for available commands");
        Console.WriteLine();

        Task.Run(() =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    Message incoming = _queue.DequeueIncomingBlocking(_cts.Token);

                    string roomPrefix = !string.IsNullOrEmpty(incoming.Room) ? $" {incoming.Room}" : "";
                    Console.WriteLine($"[{incoming.Timestamp:HH:mm:ss}]{roomPrefix} {incoming.Sender}: {incoming.Content}");
                }
            }
            catch (OperationCanceledException)
            {
                //empty
            }
        });

        // Main loop - handle user input
        bool running = true;
        while (running)
        {
            // TODO: Implement the main input loop
            // 1. Read a line from the console
            // 2. Skip empty input
            // 3. Parse the input using ConsoleUI.ParseCommand()
            // 4. Handle the command based on CommandType:
            //    - Connect: Call await _client.ConnectAsync(host, port)
            //    - Listen: Call _server.Start(port)
            //    - ListPeers: Display connection status
            //    - History: Show message history (Sprint 3)
            //    - Quit: Set running = false
            //    - Not a command: Send as a message

            var input = Console.ReadLine();
            if (string.IsNullOrEmpty(input)) continue;

            // Temporary basic command handling - replace with full implementation
            CommandResult cmdres = _ui.ParseCommand(input);
            if (cmdres.IsCommand == false)
            {
                SendMessage(cmdres.Message ?? input);
                continue;
            }
            switch (cmdres.CommandType)
            {
                case CommandType.Help:
                    ShowHelp();
                    break;

                case CommandType.Quit:
                    running = false;
                    break;

                case CommandType.History:
                    _history.ShowHistory(20);
                    break;

                case CommandType.Peers:
                    var known = _discovery.GetKnownPeers();
                    foreach (var p in known)
                    {
                        bool alive = _heartbeat.IsAlive(p.Id);
                        Console.WriteLine($"{p.Id} - {p.Address}:{p.Port} [{(alive ? "ALIVE" : "STALE")}]");
                    }
                    break;

                case CommandType.Listen:
                    int port = int.Parse(cmdres.Args[0]);
                    _server.Start(port);
                    _discovery.Start(port);
                    _heartbeat.Start();
                    StartHeartbeatSender();
                    break;

                case CommandType.Connect:
                    if (cmdres.Args != null && cmdres.Args.Length > 1)
                    {
                        Console.WriteLine($"Connecting to {cmdres.Args[0]}:{cmdres.Args[1]}...");

                        bool connected = await _client!.ConnectAsync(cmdres.Args[0], int.Parse(cmdres.Args[1]));

                        if (connected)
                        {
                            lock (_outgoingPeers)
                            {
                                if (!_outgoingPeers.Contains(_client))
                                    _outgoingPeers.Add(_client);
                            }
                        }

                        Console.WriteLine(connected ? "Connected!" : "Failed!");
                    }
                    break;

                case CommandType.Unknown:
                    if (cmdres.Message == "debug")
                    {
                        if (_server != null)
                        {
                            _server.DebugMode = !_server.DebugMode;
                            Console.WriteLine($"Server debug mode: {(_server.DebugMode ? "ON" : "OFF")}");
                        }
                        else
                        {
                            Console.WriteLine("No server running.");
                        }
                    }
                    else
                    {
                        Console.WriteLine("unknown command");
                    }
                    break;

                case CommandType.CreateRoom:
                    if (cmdres.Args != null && cmdres.Args.Length > 0)
                    {
                        string room = cmdres.Args[0];
                        if (_server != null && _server.IsListening)
                        {
                            bool created = _server.CreateRoom(room);
                            Console.WriteLine(created
                                ? $"Room {room} created."
                                : $"Room {room} already exists.");
                        }
                        else if (_client != null && _client.IsConnected)
                        {
                            _client.Send(new Message
                            {
                                Sender = _username,
                                Content = "create",
                                Room = room,
                                Type = MessageType.RoomCommand
                            });
                        }
                        else
                        {
                            Console.WriteLine("You must be connected or running a server to create rooms.");
                        }
                    }
                    break;

                case CommandType.JoinRoom:
                    if (cmdres.Args != null && cmdres.Args.Length > 0)
                    {
                        string room = cmdres.Args[0];
                        if (_server != null && _server.IsListening)
                        {
                            if (_server.JoinRoom(room, null))
                            {
                                _joinedRooms.Add(room);
                                _activeRoom = room;
                                Console.WriteLine($"Joined room {room}.");
                            }
                            else
                            {
                                Console.WriteLine($"Could not join room {room} (doesn't exist or already joined).");
                            }
                        }
                        else if (_client != null && _client.IsConnected)
                        {
                            _client.Send(new Message
                            {
                                Sender = _username,
                                Content = "join",
                                Room = room,
                                Type = MessageType.RoomCommand
                            });
                            _joinedRooms.Add(room);
                            _activeRoom = room;
                        }
                        else
                        {
                            Console.WriteLine("You must be connected or running a server to manage rooms.");
                        }
                    }
                    break;

                case CommandType.LeaveRoom:
                    if (cmdres.Args != null && cmdres.Args.Length > 0)
                    {
                        string room = cmdres.Args[0];
                        if (_server != null && _server.IsListening)
                        {
                            _server.LeaveRoom(room, null);
                            _joinedRooms.Remove(room);
                            if (_activeRoom == room)
                                _activeRoom = _joinedRooms.FirstOrDefault();
                            Console.WriteLine($"Left room {room}.");
                        }
                        else if (_client != null && _client.IsConnected)
                        {
                            _client.Send(new Message
                            {
                                Sender = _username,
                                Content = "leave",
                                Room = room,
                                Type = MessageType.RoomCommand
                            });
                            _joinedRooms.Remove(room);
                            if (_activeRoom == room)
                                _activeRoom = _joinedRooms.FirstOrDefault();
                        }
                    }
                    break;

                case CommandType.Rooms:
                    if (_server != null && _server.IsListening)
                    {
                        var rooms = _server.GetRooms();
                        if (rooms.Count == 0)
                        {
                            Console.WriteLine("No rooms available.");
                        }
                        else
                        {
                            Console.WriteLine("Available rooms:");
                            foreach (var r in rooms)
                                Console.WriteLine($"  {r}");
                        }
                    }
                    else if (_client != null && _client.IsConnected)
                    {
                        _client.Send(new Message
                        {
                            Sender = _username,
                            Content = "list",
                            Type = MessageType.RoomCommand
                        });
                    }
                    else
                    {
                        Console.WriteLine("You must be connected or running a server to list rooms.");
                    }
                    break;

                case CommandType.RoomMessage:
                    if (cmdres.Args != null && cmdres.Args.Length > 0 && cmdres.Message != null)
                    {
                        string room = cmdres.Args[0];
                        var msg = new Message
                        {
                            Sender = _username,
                            Content = cmdres.Message,
                            Room = room
                        };
                        if (_server != null && _server.IsListening)
                        {
                            _server.SendToRoom(room, msg);
                        }
                        else if (_client != null && _client.IsConnected)
                        {
                            _client.Send(msg);
                        }
                        _queue.EnqueueIncoming(msg);
                    }
                    break;
            }
        }

        // TODO: Implement graceful shutdown
        // 3. (Sprint 3) Stop peer discovery and heartbeat monitor
        _cts.Cancel();
        _queue.CompleteAdding();
        _server?.Stop();
        _client?.Disconnect();

        Console.WriteLine("Goodbye!");
    }

    /// <summary>
    /// Display help information.
    /// Replace this with ConsoleUI.ShowHelp() once implemented.
    /// </summary>
    private static void ShowHelp()
    {
        Console.WriteLine("\nAvailable Commands:");
        Console.WriteLine("  /connect <ip> <port>  - Connect to another messenger");
        Console.WriteLine("  /listen <port>        - Start listening for connections");
        Console.WriteLine("  /peers                - Show connection status");
        Console.WriteLine("  /create #room         - Create a new chat room");
        Console.WriteLine("  /join #room           - Join an existing room");
        Console.WriteLine("  /leave #room          - Leave a room");
        Console.WriteLine("  /rooms                - List available rooms");
        Console.WriteLine("  /msg #room message    - Send a message to a specific room");
        Console.WriteLine("  /history              - View message history (Sprint 3)");
        Console.WriteLine("  /quit                 - Exit the application");
        Console.WriteLine();
    }

    // Helper methods HandlePeers() and SendMessage() wrriten by Alex Vasilcoiu

    private static void HandlePeers()
    {
        bool hasServer = _server != null;
        bool hasClient = _client != null && _client.IsConnected;

        if (!hasServer && !hasClient)
        {
            Console.WriteLine("No active connections.");
            return;
        }

        if (hasServer && _server != null)
        {
            Console.WriteLine($"  [server] Listening on port {_server.Port}");
        }

        if (hasClient && _client != null)
        {
            Console.WriteLine($"  [client] Connected to {_clientEndpoint}");
        }
    }


    private static void SendMessage(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var msg = new Message
        {
            Id = Guid.NewGuid(),
            Sender = _discovery.LocalPeerId,
            Content = content,
            Timestamp = DateTime.Now,
            Type = MessageType.Text,
            Room = _activeRoom
        };

        BroadcastToMesh(msg);

        _history.SaveMessage(msg);
        _queue.EnqueueIncoming(msg);
    }
    
    private static void BroadcastToMesh(Message msg)
    {
        _server?.Broadcast(msg);

        List<Client> snapshot;
        lock (_outgoingPeers)
        {
            snapshot = _outgoingPeers.ToList();
        }

        foreach (var client in snapshot)
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Send(msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[System] Failed to send to an outgoing peer: {ex.Message}");
            }
        }
    }

    private static void StartHeartbeatSender()
    {
        Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ping = new Message
                {
                    Sender = _discovery.LocalPeerId,
                    Type = MessageType.Heartbeat,
                    Content = "ping"
                };

                BroadcastToMesh(ping);

                await Task.Delay(_heartbeat.HeartbeatInterval);
            }
        });
    }
}
