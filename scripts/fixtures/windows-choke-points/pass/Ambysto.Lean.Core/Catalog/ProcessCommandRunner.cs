// Tripwire fixture: not compiled. The one file that starts a process (plan "Starting a process").
class F
{
    void M()
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = false }; using var p = Process.Start(psi); p.StartInfo.CreateNoWindow = true; var q = new Process();
    }
}
