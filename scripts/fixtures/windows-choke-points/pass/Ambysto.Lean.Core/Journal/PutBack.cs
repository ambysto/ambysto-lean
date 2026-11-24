// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        MoveFileEx(from, to, MOVEFILE_WRITE_THROUGH); File.Move(a, b);
    }
}
