using System;
using System.IO;
using System.Threading;
internal static class Worker
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 1;
        byte[] bytes = new byte[96 * 1024 * 1024];
        for (int i = 0; i < bytes.Length; i += 4096) bytes[i] = 123;
        File.WriteAllText(args[0], "ready");
        for (int wait = 0; wait < 600; wait++)
        {
            if (File.Exists(args[0] + ".touch"))
            {
                bool valid = true;
                for (int i = 0; i < bytes.Length; i += 4096) if (bytes[i] != 123) valid = false;
                File.WriteAllText(args[0] + ".done", valid ? "OK" : "FAIL"); break;
            }
            Thread.Sleep(100);
        }
        GC.KeepAlive(bytes); return 0;
    }
}
