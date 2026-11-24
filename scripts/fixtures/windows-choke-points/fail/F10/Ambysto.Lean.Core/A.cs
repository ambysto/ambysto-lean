// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.DeleteOnClose);
    }
}
