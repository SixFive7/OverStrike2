using static OverStrike2.TestKit.T;

namespace OverStrike2.Core.SelfTest;

/// <summary>A log's tail (Tail.cs), which both server programs read their logs with.</summary>
static class TailTests
{
    public static void Run()
    {
        var path = Path.Combine(Dir("tail"), "console.log");
        File.WriteAllText(path, "old line\n");
        var tail = new Tail(path);
        File.AppendAllText(path, "one\ntw");
        var first = tail.Lines();
        File.AppendAllText(path, "o\r\nthree\n");
        var second = tail.Lines();
        File.WriteAllText(path, "fresh\n");
        var third = tail.Lines();
        Check("tail: new lines only, a line cut short kept for later, a truncated log read from its start",
              first is ["one"] && second is ["two", "three"] && third is ["fresh"] && tail.Lines().Count == 0);
        File.WriteAllText(path, "");
        var small = new Tail(path, 8);
        File.AppendAllText(path, "abcdefghij\nk\n");
        var cut = small.Lines();
        var rest = small.Lines();
        Check("tail: one look reads at most its limit, the rest comes at the next", cut.Count == 0 && rest is ["abcdefghij", "k"] && small.Lines().Count == 0);
        Check("tail: a file that is not there has no lines, and is no error", new Tail(Path.Combine(Dir("tail-none"), "none.log")).Lines().Count == 0);
    }
}
