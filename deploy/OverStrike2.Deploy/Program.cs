namespace OverStrike2.Deploy;

/// <summary>os2-deploy: the deploy tooling of Over Strike 2 in one program. Every command reads the deployment from the private config
/// (deploy/config.example.toml shows every key): $OVERSTRIKE2_CONFIG, else .private/config.toml in this checkout; credentials come from
/// the environment ("env:NAME" values), and nothing here prints one.</summary>
static class Program
{
    const string Usage = """
usage: os2-deploy <command> ...    (dotnet run --project deploy/OverStrike2.Deploy -- <command> ..., from the repository's root)

  bridge check|build|images|swap|update|rollback [--tag T]   the bridge's image and container
  data [--apply] [--force]                            the bridge's hand-made data files
  plugin build|ship [--apply]                         the game server's plugin
  server build [--image] | ship [--apply] [--force]   os2-server, os2-sync, pre.sh and the mode cfgs
  workshop hud-thumbs|hud-build|hud-query|hud-public|hud-publish|map ...   the Workshop items
  thumbs [--export F] [--queues F] [--ahead F] [--workers N]                 the HUD's map pictures
  release live <tag> [--no-plugin] [--under-lock] [--dry-run]               a release on the server
  release publish --note N --client-test RUN [--under-lock] [--dry-run]     a content update of the HUD item
  check guard [--allow-kick] | verify                 before and after a redeploy of the whole stack (read-only)
  probe <verb> ...                                    the client harness's server half: the lock, RCON, the probe plugin
  testcopy <verb> ...                                 the insecure test copy of the game server and the candidate bridge
  bridgepost <verb> ...                               what only the watcher or other players send a candidate bridge
  --selftest [suite...]                               every offline check (deploy, release, check, testcopy, testcopy-bridge, bridgepost, ...)

  every command takes --config FILE (default: $OVERSTRIKE2_CONFIG, else .private/config.toml)
""";

    static int Main(string[] args)
    {
        Out.Utf8();
        try
        {
            return Dispatch(args);
        }
        catch (Exit e)
        {
            if (e.Text is not null)
                Console.Error.WriteLine(e.Text);
            return e.Code;
        }
        catch (Exception e)
        {
            Console.Out.Flush();
            Console.Error.WriteLine($"error: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    public static int Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.Write(Usage);
            return 2;
        }
        var rest = args[1..];
        switch (args[0])
        {
            case "-h":
            case "--help":
                Console.Out.Write(Usage);
                return 0;
            case "bridge":
                return Bridge.Command(rest);
            case "data":
                return Data.Command(rest);
            case "plugin":
                return Plugin.Command(rest);
            case "server":
                return ServerShip.Command(rest);
            case "workshop":
                return Workshop.Command(rest);
            case "release":
                return Release.Command(rest);
            case "check":
                return Check.Command(rest);
            case "thumbs":
                return Thumbs.Command(rest);
            case "probe":
                return Probe.Command(rest);
            case "testcopy":
                return TestCopy.Command(rest);
            case "bridgepost":
                return BridgePost.Command(rest);
            case "--selftest":
                return SelfTest.Command(rest);
            default:
                Console.Error.Write(Usage);
                Console.Error.WriteLine($"error: unknown command '{args[0]}'");
                return 2;
        }
    }
}
