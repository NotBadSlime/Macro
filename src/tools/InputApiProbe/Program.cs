namespace InputApiProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.Error.WriteLine("Usage: InputApiProbe capture|capture-paired|inject|--receiver <name> [options]");
            return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
