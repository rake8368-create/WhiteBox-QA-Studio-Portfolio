using System;

internal static class EmptyCatch
{
    public static void Run()
    {
        try { Console.WriteLine("작업"); }
        catch (Exception)
        {
            // 나중에 예외 처리 추가
        }
    }
}
