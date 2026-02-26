// [Your Name Here]
// CSCI 251 - Secure Distributed Messenger
//
// SPRINT 1: Threading & Basic Networking
// Due: Week 5 | Work on: Weeks 3-4
//

using SecureMessenger.Core;

namespace SecureMessenger.UI;

/// <summary>
/// Console-based user interface.
/// Handles user input parsing and message display.
///
/// Supported Commands:
/// - /connect host port  - Connect to another messenger
/// - /listen port        - Start listening for connections
/// - /peers              - Show connection status
/// - /history            - View message history (Sprint 3)
/// - /quit or /exit      - Exit the application
/// - Any other text      - Send as a message
/// </summary>
public class ConsoleUI
{
    /// <summary>
    /// Display a received message to the console.
    ///
    /// TODO: Implement the following:
    /// 1. Format the message nicely, e.g.: "[14:30:25] Alice: Hello!"
    /// 2. Use message.Timestamp.ToString("HH:mm:ss") for time format
    /// 3. Print to console
    /// </summary>
    public void DisplayMessage(Message message)
    {
        string output = $"[{message.Timestamp.ToString("HH:mm:ss")}] {message.Sender} {message.Content}";
        Console.WriteLine($"{output}");
    }

    /// <summary>
    /// Display a system message to the console.
    ///
    /// TODO: Implement the following:
    /// 1. Print in a distinct format, e.g.: "[System] Server started on port 5000"
    /// </summary>
    public void DisplaySystem(string message)
    {
        Console.WriteLine($"[System] {message}");
    }

    /// <summary>
    /// Show available commands to the user.
    ///
    /// TODO: Implement the following:
    /// 1. Print a formatted help message showing all available commands
    /// 2. Include: /connect, /listen, /peers, /history, /quit
    /// </summary>
    public void ShowHelp()
    {
		Console.WriteLine("/connect <ip> <port> - Connect to a peer at the specified address");
        Console.WriteLine("/listen <port> - Start listening for incoming connections");
        Console.WriteLine("/peers - List all known peers");
        Console.WriteLine("/history - View message history");
        Console.WriteLine("/quit - Exit the application");
    }

    /// <summary>
    /// Parse user input and return a CommandResult.
    ///
    /// TODO: Implement the following:
    /// 1. Check if input starts with "/" - if not, it's a regular message:
    ///    - Return CommandResult with IsCommand = false, Message = input
    ///
    /// 2. If it's a command, split by spaces and parse:
    ///    - "/connect host port" -> CommandType.Connect with Args = [host, port]
    ///    - "/listen port" -> CommandType.Listen with Args = [port]
    ///    - "/peers" -> CommandType.Peers
    ///    - "/history" -> CommandType.History
    ///    - "/quit" or "/exit" -> CommandType.Quit
    ///    - "/help" -> CommandType.Help
    ///    - Unknown command -> CommandType.Unknown with error message
    ///
    /// 3. Validate arguments:
    ///    - /connect requires 2 args (host and port)
    ///    - /listen requires 1 arg (port)
    ///
    /// Hint: Use input.Split(' ', StringSplitOptions.RemoveEmptyEntries)
    /// Hint: Use a switch expression for clean command matching
    /// </summary>
    public CommandResult ParseCommand(string input)
    {
        CommandResult cmdres = new();
        if ( input.StartsWith("/") ){
            switch(input){
                case "/help":
                cmdres.CommandType = CommandType.Help;
                break;

                case string s when (s == "/quit") || (s == "/exit"):
                cmdres.CommandType = CommandType.Quit;
                break;

                case "/history":
                cmdres.CommandType = CommandType.History;
                break;

                case "/peers":
                cmdres.CommandType = CommandType.Peers;
                break;

                case string s when s.StartsWith("/listen"):
                string[] command = input.Split(" ", 2, StringSplitOptions.RemoveEmptyEntries);
                if (command.Length() != 2){
                    Console.WriteLine("uh oh");
                    break;
                string[] port = {command[1]};
                cmdres.CommandType = CommandType.Listen;
                cmdres.Args = port;
                break;

                case string s when s.StartsWith("/connect"):
                string[] connectcoms = input.Split(" ", 3, StringSplitOptions.RemoveEmptyEntries);
                if (connectcoms.Length() != 3){
                    Console.WriteLine("done goofed");
                    break;
                }
                string[] args = {connectcoms[1], connectcoms[2]};
                cmdres.CommandType = CommandType.Connect;
                cmdres.Args = args;
                break;
            }
        }
        else{
            cmdres.IsCommand = false;
            cmdres.Message = input;
        }

        return cmdres;
    }
}

/// <summary>
/// Types of commands the user can enter
/// </summary>
public enum CommandType
{
    Unknown,
    Connect,
    Listen,
    Peers,
    History,
    Help,
    Quit
}

/// <summary>
/// Result of parsing a user input line
/// </summary>
public class CommandResult
{
    /// <summary>True if the input was a command (started with /)</summary>
    public bool IsCommand { get; set; }

    /// <summary>The type of command parsed</summary>
    public CommandType CommandType { get; set; }

    /// <summary>Arguments for the command (e.g., host and port for /connect)</summary>
    public string[]? Args { get; set; }

    /// <summary>The message content (for non-commands or error messages)</summary>
    public string? Message { get; set; }
}
