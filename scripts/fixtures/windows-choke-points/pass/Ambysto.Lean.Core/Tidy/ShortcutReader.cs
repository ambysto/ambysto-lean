// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        var link = (IShellLinkW)new ShellLink(); ((IPersistFile)link).Load(path, 0);
    }
}
