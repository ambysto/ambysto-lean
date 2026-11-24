// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        using var k = Registry.CurrentUser.OpenSubKey(path, true);
    }
}
