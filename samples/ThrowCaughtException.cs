using System;

internal static class ThrowCaughtException
{
    public static void Run()
    {
        try { throw new InvalidOperationException("예제"); }
        catch (Exception ex)
        {
            throw ex;
        }
    }
}
