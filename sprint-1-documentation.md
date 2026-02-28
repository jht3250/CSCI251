# Sprint 1 Documentation

## Secure Distributed Messenger

**Team Name:** Group 29

**Team Members:**

- Alex Vasilcoiu - Helper functions in Program.cs, Client.cs, documentation
- John Treon - TcpServer, TcpClientHandler, Server.cs
- Owen Deng - Main loop in Program.cs & UI
- Ethan Crawford - Server & Client events in Program.cs

**Date:** 2/27/2026

---

## Build Instructions

### Prerequisites

- .NET 9.0 SDK
- A terminal that supports running two instances side-by-side

### Building the Project

```
dotnet build SecureMessenger.csproj
```

---

## Run Instructions

```
dotnet run --project SecureMessenger.csproj
```

### Starting the Application

You need **two terminal instances** to test messaging. In each terminal, run:

```
dotnet run --project SecureMessenger.csproj
```

**Terminal 1 (Server):**

```
/listen 5000
```

**Terminal 2 (Client):**

```
/connect 127.0.0.1 5000
```

Once connected, type any plain text to send a message to the other instance.

### Command Line Arguments (if any)

No command line arguments are used. All interaction happens through in-app commands.

## Application Commands

| Command                | Description                     | Example                   |
| ---------------------- | ------------------------------- | ------------------------- |
| `/connect <ip> <port>` | Connect to a listening peer     | `/connect 127.0.0.1 5000` |
| `/listen <port>`       | Start listening for connections | `/listen 5000`            |
| `/peers`               | Show active connection status   | `/peers`                  |
| `/help`                | Display available commands      | `/help`                   |
| `/history`             | View message history (Sprint 3) | `/history`                |
| `/quit` or `/exit`     | Exit the application            | `/quit`                   |

---

## Architecture Overview

### Threading Model

The application uses an async/await task-based threading model with multiple concurrent tasks:

- **Main Thread (UI Thread):** Runs the main input loop. Reads user input from `Console.ReadLine()`, parses commands via `ConsoleUI.ParseCommand()`, and dispatches them to the appropriate handler (connect, listen, send, etc.).
- **Accept Task (Server):** When `/listen` is called, `Server.Start()` launches `AcceptClientsAsync()` on a background task via `Task.Run()`. This task loops on `TcpListener.AcceptTcpClientAsync()`, waiting for incoming connections. Each accepted connection spawns its own receive task.
- **Server Receive Task(s):** One per connected client. `ReceiveFromClientAsync()` runs on a background task for each accepted `TcpClient`, reading messages using length-prefix framing (4-byte length header + JSON payload). When a message is received, it invokes the `OnMessageReceived` event.
- **Client Receive Task:** When `/connect` succeeds, `Client.ConnectAsync()` launches `ReceiveAsync()` on a background task. This task reads incoming messages from the server using the same length-prefix framing protocol and invokes `OnMessageReceived`.

### Thread Communication

- **Events (`Action<T>`):** The `Server` and `Client` classes expose events (`OnClientConnected`, `OnClientDisconnected`, `OnMessageReceived`, `OnConnected`, `OnDisconnected`). `Program.cs` subscribes to these events to display connection status and incoming messages on the console.
- **CancellationToken:** Each component creates a `CancellationTokenSource` to enable graceful shutdown. When `/quit` is called, `Server.Stop()` and `Client.Disconnect()` cancel their tokens, which interrupts any blocking async reads.
- **Locking (`lock`):** The `Server` class protects its `_clients` list with `_clientsLock` to ensure thread-safe access when adding, removing, or iterating over connected clients during broadcast.

### Message Protocol

Messages are exchanged using **length-prefix framing**:

1. Messages are serialized to JSON using `System.Text.Json.JsonSerializer`
2. The JSON string is encoded to bytes using UTF-8
3. A 4-byte length prefix (`BitConverter.GetBytes`) is written first
4. The payload bytes follow immediately after

On the receiving end, the process is reversed: read 4 bytes for the length, then read that many bytes for the JSON payload, and deserialize into a `Message` object.

### Thread-Safe Message Queue

The `MessageQueue` class (in `Core/MessageQueue.cs`) is available as an optional component using the Producer/Consumer pattern with `BlockingCollection<T>`. For Sprint 1, messages are handled directly in event handlers rather than through the queue, keeping the architecture simple. The queue is available for future sprints if more sophisticated message processing pipelines are needed.

---

## Features Implemented

- [x] Multi-threaded architecture (async/await tasks for accept, receive, and UI)
- [x] TCP server (listen for connections via `/listen <port>`)
- [x] TCP client (connect to peers via `/connect <ip> <port>`)
- [x] Send/receive text messages with length-prefix framing and JSON serialization
- [x] Graceful disconnection handling (`/quit` cancels tokens, closes streams and clients)
- [x] Console UI with command parsing (`/help`, `/connect`, `/listen`, `/peers`, `/quit`)
- [x] Thread-safe client list in Server using `lock`
- [x] Event-driven notifications for connect/disconnect/message events
- [x] Connection status display via `/peers`
- [ ] Thread-safe message queue (available but not used in Sprint 1)

---

## Testing Performed

### Test Cases

| Test                        | Expected Result                                  | Actual Result                                      | Pass/Fail |
| --------------------------- | ------------------------------------------------ | -------------------------------------------------- | --------- |
| Two instances can connect   | Connection established, both sides show feedback | Server prints client connected, client confirms    | Pass      |
| Messages sent and received  | Message appears on other instance                | Messages display with timestamp and sender         | Pass      |
| Server broadcasts to client | Server-side message reaches connected client     | Client receives and displays the message           | Pass      |
| Client sends to server      | Client-side message reaches listening server     | Server receives and displays the message           | Pass      |
| Disconnection handled       | No crash, appropriate disconnect message shown   | Disconnect event fires, message displayed          | Pass      |
| `/help` displays commands   | All available commands listed                    | Commands displayed correctly                       | Pass      |
| `/quit` exits cleanly       | Application shuts down, connections closed       | Server stops, client disconnects, "Goodbye!" shown | Pass      |

---

## Known Issues

| Issue                       | Description                                                                 | Workaround                                                               |
| --------------------------- | --------------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| Single client connection    | `Client` only supports one outgoing connection at a time                    | Use `/listen` on both sides for bidirectional multi-peer (Sprint 3)      |
| No input validation on port | `/listen abc` or `/connect host notaport` will throw an unhandled exception | Ensure port arguments are valid integers                                 |
| No reconnection support     | If a connection drops, it cannot be re-established without restarting       | Restart the application and reconnect (Sprint 3 will add auto-reconnect) |
| `/history` not implemented  | The command is parsed but does nothing in Sprint 1                          | Will be implemented in Sprint 3                                          |

---

## Video Demo Checklist

Your demo video (3-5 minutes) should show:

- [ ] Starting two instances of the application
- [ ] One instance runs `/listen 5000`
- [ ] Other instance runs `/connect 127.0.0.1 5000`
- [ ] Both sides see connection confirmation messages
- [ ] Sending messages from client to server
- [ ] Sending messages from server to client
- [ ] Disconnecting gracefully with `/quit`
- [ ] (Optional) Showing thread-safe behavior under load
