using System.Text.Json.Nodes;

namespace OverStrike2.Deploy;

/// <summary>What the Python scripts had from Python itself and this program brings of its own: the TOML reader (tomllib), JSON written as
/// json.dumps writes it, Python's repr and splitlines, the A2S reply's reading. No Python test held these; they hold the ports' ground.</summary>
static class BaseTests
{
    public static void Run()
    {
        var check = SelfTest.Check;

        // ---- the TOML reader, on what the private config uses and what TOML allows besides
        var t = Toml.Parse(""""
# a comment
[docker]
api = "https://docker.example.com:2376"   # after a value
tls_verify = false
[docker.headers]
X-API-Key = "env:OVERSTRIKE2_DOCKER_API_KEY"

[bridge]
data_dir = 'back\slash\kept'
"quoted.key" = "v"
multi = [
  "a",   # inside an array
  'b',
]
n = 1_000
neg = -17
hex = 0xff
f = 1.5e3
when = 1979-05-27T07:32:00Z
s = "tab\there \u00e9 \"q\""
ml = """
first
second \
  joined"""
lit = '''
no \escape'''

[[check.audit]]
name = "one"
argv = ["python", "-I", 'x.py']
env = { K = "env:V", N = 2 }
[[check.audit]]
name = "two"
"""");
        Dictionary<string, object?> T(string key) => (Dictionary<string, object?>)t[key]!;
        var bridge = T("bridge");
        var audits = (List<object?>)T("check")["audit"]!;
        check("toml: tables, a sub-table, an env: value and a bool", (string?)T("docker")["api"] == "https://docker.example.com:2376" && (bool?)T("docker")["tls_verify"] == false
              && (string?)((Dictionary<string, object?>)T("docker")["headers"]!)["X-API-Key"] == "env:OVERSTRIKE2_DOCKER_API_KEY");
        check("toml: a literal string keeps its backslashes, a quoted key keeps its dot, a multi-line array with comments and a trailing comma",
              (string?)bridge["data_dir"] == @"back\slash\kept" && (string?)bridge["quoted.key"] == "v" && ((List<object?>)bridge["multi"]!).SequenceEqual(["a", "b"]));
        check("toml: integers with underscores, a sign and in hex, a float, a date-time kept as its text",
              (long?)bridge["n"] == 1000 && (long?)bridge["neg"] == -17 && (long?)bridge["hex"] == 255 && (double?)bridge["f"] == 1500.0 && (string?)bridge["when"] == "1979-05-27T07:32:00Z");
        check("toml: escapes in a basic string, a multi-line string's first newline and its line-ending backslash, a multi-line literal",
              (string?)bridge["s"] == "tab\there \u00e9 \"q\"" && (string?)bridge["ml"] == "first\nsecond joined" && (string?)bridge["lit"] == "no \\escape");
        check("toml: an array of tables with an inline table", audits.Count == 2 && (string?)((Dictionary<string, object?>)audits[0]!)["name"] == "one"
              && (string?)((Dictionary<string, object?>)((Dictionary<string, object?>)audits[0]!)["env"]!)["K"] == "env:V" && (string?)((Dictionary<string, object?>)audits[1]!)["name"] == "two");
        var errors = new[] { "a = ", "[t]\n[t]", "a = 1\na = 2", "a = \"open", "a = [1 2]", "a = 01" }.Count(bad =>
        {
            try
            {
                Toml.Parse(bad);
                return false;
            }
            catch (TomlError)
            {
                return true;
            }
        });
        check($"toml: a broken text is an error, not a guess ({errors} of 6)", errors == 6);
        var example = Toml.Parse(File.ReadAllText(Repo.P("deploy", "config.example.toml")));
        check("toml: deploy/config.example.toml reads, every table of it",
              new[] { "docker", "build", "bridge", "server", "check", "stack", "release", "workshop", "client", "rehearse" }.All(example.ContainsKey));

        // ---- the config: a dotted name, env: from the environment, a required value
        var scratch = SelfTest.Scratch("base-config-");
        try
        {
            var path = Path.Combine(scratch, "config.toml");
            File.WriteAllText(path, "[a]\nb = \"env:OS2_BASE_TEST\"\nc = \"\"\nlist = [\"x\"]\n");
            Environment.SetEnvironmentVariable("OS2_BASE_TEST", "the value");
            var cfg = new Config(path);
            var missing = SelfTest.Refusal(() => cfg.Get("a.nope", required: true));
            var empty = SelfTest.Refusal(() => cfg.Get("a.c", required: true));
            check("config: env: is read from the environment, a missing or empty required value ends with exit 2",
                  cfg.Str("a.b") == "the value" && cfg.Str("a.nope", "dflt") == "dflt" && cfg.List("a.list").Count == 1 && missing == "2" && empty == "2");
            Environment.SetEnvironmentVariable("OS2_BASE_TEST", null);
            check("config: an env: value whose variable is not set is empty, and required ends with exit 2",
                  cfg.Str("a.b") == "" && SelfTest.Refusal(() => cfg.Get("a.b", required: true)) == "2");
        }
        finally
        {
            SelfTest.Remove(scratch);
        }

        // ---- JSON as json.dumps writes it, Python's repr, splitlines and its float formatting
        var node = Json.Parse("{\"b\": [1, 2.5, true, null, \"\u00e9\\n\"], \"a\": {}, \"c\": 1e20}");
        check("json: written as json.dumps writes it - the separators, ASCII escapes, the order kept, floats as Python prints them",
              Json.Dumps(node) == "{\"b\": [1, 2.5, true, null, \"\\u00e9\\n\"], \"a\": {}, \"c\": 1e+20}");
        check("json: with indent, as json.dumps(..., indent=1)", Json.Dumps(Json.O("""{"a": [1], "b": {}}"""), 1) == "{\n \"a\": [\n  1\n ],\n \"b\": {}\n}");
        check("json: the last of two equal keys wins, in the first one's place", Json.Dumps(Json.Parse("{\"a\": 1, \"b\": 2, \"a\": 3}")) == "{\"a\": 3, \"b\": 2}");
        check("py: repr of str, None, a list of tuples and a one-item tuple",
              Py.Repr("it's") == "\"it's\"" && Py.Repr("a\tb") == "'a\\tb'" && Py.Repr(null) == "None"
              && Py.Repr(new List<NetAddress> { new("n", "192.0.2.1", null) }) == "[('n', '192.0.2.1', None)]" && Py.Repr(new A2sAnswer("s", "m", 0, 1)) == "('s', 'm', 0, 1)");
        check("py: floats as repr writes them", Py.Float(100.0) == "100.0" && Py.Float(1e16) == "1e+16" && Py.Float(1e15) == "1000000000000000.0"
              && Py.Float(0.0001) == "0.0001" && Py.Float(1e-5) == "1e-05" && Py.Float(-2.5) == "-2.5" && Py.Float(0.1 + 0.2) == "0.30000000000000004");
        check("py: splitlines and strip, with Python's line breaks", Py.SplitLines("a\r\nb\rc\n\nd\u2028e\n").SequenceEqual(["a", "b", "c", "", "d", "e"])
              && Py.Strip(" \t x \n") == "x" && Py.Slice("abc", 7, 19) == "" && Py.Slice("abcdef", -2) == "ef");
        check("fmt: rounded half to even, as Python prints a float", Fmt.Fixed(12.5, 0) == "12" && Fmt.Fixed(13.5, 0) == "14" && Fmt.Fixed(0.125, 2) == "0.12"
              && Fmt.Quote("{\"a\": [\"x/y\"]}") == "%7B%22a%22%3A%20%5B%22x/y%22%5D%7D");
        var jsonc = Json.Jsonc("{ \"k\": [1,], } // done");
        check("jsonc: comments and trailing commas", Json.Dumps(jsonc) == "{\"k\": [1]}");
    }
}
