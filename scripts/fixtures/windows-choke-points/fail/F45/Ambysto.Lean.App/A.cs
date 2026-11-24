// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
    }
}
