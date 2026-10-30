using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Threading.Tasks;

internal static class EchoBridge
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int code);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr handle);
    public static int Main(string[] args)
    {
        string mode = args.Length == 0 ? "console" : args[0];
        if(mode == "console") Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        TextReader input = mode == "raw" ? new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 4096)
            : mode == "handle" ? new StreamReader(new FileStream(new SafeFileHandle(GetStdHandle(-10),false),FileAccess.Read,4096,false),new UTF8Encoding(false),false,4096)
            : Console.In;
        Console.WriteLine("ready:" + Process.GetCurrentProcess().Id + ":mode="+mode+":redirected="+Console.IsInputRedirected+":handleType="+GetFileType(GetStdHandle(-10)));
        Console.Out.Flush();
        Task reader = Task.Run(delegate {
            string line;
            while ((line = input.ReadLine()) != null) {
                Console.WriteLine("received:" + line); Console.Out.Flush();
            }
        });
        if (!reader.Wait(5000)) { Console.Error.WriteLine("bounded echo timeout"); return 3; }
        reader.GetAwaiter().GetResult();
        return 0;
    }
}
