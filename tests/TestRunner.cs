using System;

namespace Nulluxion.Tests
{
    internal static class TestRunner
    {
        [STAThread]
        private static int Main()
        {
            try
            {
                Console.WriteLine("PASS: " + HardwarePowerTests.Run() + " isolated hardware worker assertions.");
                DisplaySleepTests.Run();
                TrayAppTests.Run();
                Console.WriteLine("All isolated tests passed (no physical monitor commands).");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
