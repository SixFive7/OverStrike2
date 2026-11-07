using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>What A2S says of the test engine, as a test load reads it.</summary>
public sealed record EngineInfo(string Map, int Players, int Bots, int Humans);

/// <summary>The test copy of the server, as the steps use it: a test stands in its own.</summary>
public interface ITestEngine
{
    int Starts { get; set; }
    ILines? Tail { get; }
    bool Alive();
    (bool Ok, string Why) Start();
    void Stop();
    string? Rcon(string command);
    EngineInfo? A2s();
}
