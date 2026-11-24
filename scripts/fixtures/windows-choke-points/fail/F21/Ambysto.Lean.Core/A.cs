// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        SetFileInformationByHandle(h, FileRenameInfo, buf, n);
    }
}
