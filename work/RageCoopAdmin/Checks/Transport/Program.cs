using System;
using CoopControl.Shared;

internal static partial class Program
{
    private static int Main()
    {
        try
        {
            CheckCodecs();
            CheckAcknowledgements();
            CheckFragments();
            EngineTransportGuard.Install();
            EngineTransportGuard.Install();
            EngineTransportGuard.Uninstall();
            EngineTransportGuard.Uninstall();
            Console.WriteLine("Actual Core codecs and Lidgren fragment bounds/lifecycle checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { EngineTransportGuard.Uninstall(); }
    }

    private static void Assert(bool value, string message)
    { if (!value) { throw new InvalidOperationException(message); } }
}
