namespace InputApiProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string Option(string key, string fallback)
            { int at = Array.IndexOf(args, key); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }

            Native.timeBeginPeriod(1); // stable 10ms pacing; recorded in provenance
            try
            {
                switch (args.FirstOrDefault())
                {
                    case "raw-diag":
                        return RawInputDiagExperiment.Run(int.Parse(Option("--seconds", "20")), Option("--window", "message"),
                            Option("--flags", "sink"), Option("--also", ""),
                            CapturePairedExperiment.ParseKey(Option("--key", "F8")), Option("--output", "tmp/input-api"), args);
                    case "capture-paired":
                        return CapturePairedExperiment.Run(int.Parse(Option("--physical", "30")),
                            CapturePairedExperiment.ParseKey(Option("--key", "F8")), Option("--group", "main"),
                            Option("--output", "tmp/input-api"), args);
                    case "capture":
                        return CaptureExperiment.Run(int.Parse(Option("--rounds", "5")), int.Parse(Option("--per-round", "2000")),
                            Option("--output", "tmp/input-api"), args);
                    case "inject":
                        return InjectExperiment.Run(int.Parse(Option("--rounds", "5")), int.Parse(Option("--per-block", "2000")),
                            Option("--output", "tmp/input-api"), args);
                    case "--receiver":
                        ReceiverProcess.Run(args[1], int.Parse(args[2]));
                        return 0;
                    default:
                        Console.Error.WriteLine("Usage: InputApiProbe capture|capture-paired|raw-diag|inject|--receiver <name> <parentPid> [options]");
                        return 2;
                }
            }
            finally { Native.timeEndPeriod(1); }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
