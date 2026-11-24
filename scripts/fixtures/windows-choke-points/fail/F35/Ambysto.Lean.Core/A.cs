// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\x", "n", 1);
    }
}
