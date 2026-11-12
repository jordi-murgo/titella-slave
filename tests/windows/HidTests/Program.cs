// Entry point of the HID transport contract tests. Exit code 0 means every
// case passed; the same RunAll is called from PowerShell 5.1 on Windows with
// the two source units compiled by Add-Type.

namespace Titella.Slave.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            return HidProtocolTests.RunAll();
        }
    }
}
