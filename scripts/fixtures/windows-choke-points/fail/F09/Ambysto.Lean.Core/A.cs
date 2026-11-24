// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        var i = new FileDispositionInfo { DeleteFile = true };
    }
}
