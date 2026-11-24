// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        using var k = Registry.CurrentUser.OpenSubKey(path, writable: true); k.SetValue(name, cmd); k.DeleteValue(name, false);
    }
}
