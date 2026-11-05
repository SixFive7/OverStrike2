// One line of the game server's log as the bridge reads it (Program.Parse).

enum Kind { Chat, Connected, Entered, Disconnected, MapStart, MatchEnd, Shutdown }

record LogEvent(Kind Kind, string Name, string SteamId, string Text);
