namespace BatteryDiagnostic;
internal static class CompatibilityEntry
{
    static int Main()
    {
        try { CompatibilityTests.Run(); return 0; }
        catch(Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
