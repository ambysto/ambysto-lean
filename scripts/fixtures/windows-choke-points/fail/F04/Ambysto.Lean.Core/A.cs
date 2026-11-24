// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        new DirectoryInfo(p).Delete(true);
    }
}
