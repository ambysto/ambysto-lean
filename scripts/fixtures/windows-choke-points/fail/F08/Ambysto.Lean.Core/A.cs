// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }
}
