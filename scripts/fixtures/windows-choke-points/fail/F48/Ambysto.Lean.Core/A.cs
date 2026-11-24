// Tripwire fixture: not compiled.
class F
{
    void M()
    {
        dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
    }
}
