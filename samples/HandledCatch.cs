using System;

internal static class HandledCatch
{
    public static void Run()
    {
        try { Console.WriteLine("작업"); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            throw;
        }
    }
}
