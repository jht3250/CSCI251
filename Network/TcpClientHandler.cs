// [Your Name Here]
// CSCI 251 - Secure Distributed Messenger
//
// SPRINT 1: Threading & Basic Networking
// Due: Week 5 | Work on: Weeks 3-4
//
// KEY CONCEPTS USED IN THIS FILE:
//   - TcpClient: initiates outgoing connections (see HINTS.md)
//   - async/await: ConnectAsync, ReadLineAsync, WriteLineAsync
//   - StreamReader/StreamWriter: read/write text over network
//   - Locking: protect _connections dictionary
//
// CLIENT vs SERVER:
//   - TcpServer waits for others to connect TO it
//   - TcpClientHandler connects TO other servers
//   - Test: Terminal 1 runs /listen, Terminal 2 runs /connect
//

using System.Net;
using System.Net.Sockets;
using SecureMessenger.Core;

namespace SecureMessenger.Network;

/// <summary>
/// Handles outgoing TCP connections to other peers.
/// </summary>
public class TcpClientHandler
{
    private readonly Dictionary<string, Peer> _connections = new();
    private readonly object _lock = new();

    public event Action<Peer>? OnConnected;
    public event Action<Peer>? OnDisconnected;
    public event Action<Peer, Message>? OnMessageReceived;

    /// <summary>
    /// Connect to a peer at the specified address and port.
    ///
    /// TODO: Implement the following:
    /// 1. Create a new TcpClient
    /// 2. Connect asynchronously to the host and port
    /// 3. Create a Peer object with:
    ///    - Client = the TcpClient
    ///    - Stream = client.GetStream()
    ///    - Address = parsed from host string
    ///    - Port = the port parameter
    ///    - IsConnected = true
    /// 4. Add to _connections dictionary (with proper locking)
    /// 5. Invoke OnConnected event
    /// 6. Start a background task running ReceiveLoop for this peer
    /// 7. Return true on success
    /// 8. Handle SocketException - print error and return false
    /// </summary>
    public async Task<bool> ConnectAsync(string host, int port)
    {
        try
        {
            TcpClient client = new TcpClient();
            await client.ConnectAsync(host, port);

            Peer peer = new Peer
            {
                Client = client,
                Stream = client.GetStream(),
                Address = IPAddress.Parse(host),
                Port = port,
                IsConnected = true
            };

            string peerId = $"{host}:{port}";
            lock (_lock)
            {
                _connections[peerId] = peer;
            }
            OnConnected?.Invoke(peer);
            _ = Task.Run(() => ReceiveLoop(peer));
            return true;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Error connecting to {host}:{port} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Receive loop for a connected peer - reads messages until disconnection.
    ///
    /// TODO: Implement the following:
    /// 1. Create a StreamReader from the peer's stream
    /// 2. Loop while peer is connected
    /// 3. Read a line asynchronously (ReadLineAsync)
    /// 4. If line is null, connection was closed - break
    /// 5. Create a Message object with the received content
    /// 6. Invoke OnMessageReceived event
    /// 7. Handle IOException (connection lost)
    /// 8. In finally block, call Disconnect
    /// </summary>
    private async Task ReceiveLoop(Peer peer)
    {
        try
        {
            using StreamReader reader = new StreamReader(peer.Stream!);
            while (peer.IsConnected)
            {
                string? line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }
                Message message = new Message
                {
                    Content = line,
                    Timestamp = DateTime.Now,
                    Sender = peer.Address?.ToString() ?? "Unknown"
                };
                OnMessageReceived?.Invoke(peer, message);
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"IOException in ReceiveLoop for peer {peer.Address}: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
            // do nothing
        }
        finally
        {
            Disconnect($"{peer.Address}:{peer.Port}");
        }
    }

    /// <summary>
    /// Send a message to a specific peer.
    ///
    /// TODO: Implement the following:
    /// 1. Look up the peer in _connections by peerId (with proper locking)
    /// 2. If peer exists and is connected with a valid stream:
    ///    - Create a StreamWriter (with leaveOpen: true)
    ///    - Write the message line asynchronously
    ///    - Flush the writer
    /// </summary>
    public async Task SendAsync(string peerId, string message)
    {
        Peer? peer = null;
        lock (_lock)
        {
            _connections.TryGetValue(peerId, out peer);
        }
        if (peer != null && peer.IsConnected && peer.Stream != null)
        {
            try
            {
                StreamWriter writer = new StreamWriter(peer.Stream, leaveOpen: true)
                {
                    AutoFlush = true
                };
                await writer.WriteLineAsync(message);
                await writer.FlushAsync();
            }
            catch (IOException ex)
            {
                Console.WriteLine($"IOException in SendAsync for peer {peer.Address}: {ex.Message}");
                Disconnect(peerId);
            }
            catch (ObjectDisposedException)
            {
                Disconnect(peerId);
            }
        }
    }

    /// <summary>
    /// Broadcast a message to all connected peers.
    ///
    /// TODO: Implement the following:
    /// 1. Get a copy of all peers (with proper locking)
    /// 2. Loop through each peer and call SendAsync
    /// </summary>
    public async Task BroadcastAsync(string message)
    {
        List<string> peerIds;
        lock (_lock)
        {
            peerIds = _connections.Keys.ToList();
        }
        foreach (string peerId in peerIds)
        {
            await SendAsync(peerId, message);
        }
    }

    /// <summary>
    /// Disconnect from a peer.
    ///
    /// TODO: Implement the following:
    /// 1. Remove the peer from _connections (with proper locking)
    /// 2. If peer was found:
    ///    - Set IsConnected to false
    ///    - Dispose the Client and Stream
    ///    - Invoke OnDisconnected event
    /// </summary>
    public void Disconnect(string peerId)
    {
        Peer? peer = null;
        lock (_lock)
        {
            if (_connections.TryGetValue(peerId, out peer))
            {
                _connections.Remove(peerId);
            }
        }
        if (peer != null)
        {
            peer.IsConnected = false;
            try
            {
                peer.Stream?.Dispose();
            }
            catch
            {
                // do nothing
            }
            try
            {
                peer.Client?.Dispose();
            }
            catch
            {
                // do nothing
            }
            OnDisconnected?.Invoke(peer);
        }
    }

    /// <summary>
    /// Get all currently connected peers.
    /// Remember to use proper locking when accessing _connections.
    /// </summary>
    public IEnumerable<Peer> GetConnectedPeers()
    {
        lock (_lock)
        {
            return _connections.Values.ToList();
        }
    }
}
