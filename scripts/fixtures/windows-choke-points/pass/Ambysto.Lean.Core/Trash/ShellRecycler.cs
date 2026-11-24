// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        var op = (IFileOperation)new FileOperation(); op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION);
    }
}
