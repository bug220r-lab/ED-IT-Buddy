using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using EditBuddy;

internal static class PortalBridgeTests
{
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static Process Child(string pipeName)
    {
        var process = new Process { StartInfo = new ProcessStartInfo {
            FileName = Process.GetCurrentProcess().MainModule.FileName, Arguments = "--child " + pipeName,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        process.Start(); return process;
    }
    private static void Connected(NamedPipeServerStream server)
    {
        var pending = server.BeginWaitForConnection(null, null);
        if (!pending.AsyncWaitHandle.WaitOne(3000)) throw new Exception("The fake bridge did not connect.");
        server.EndWaitForConnection(pending);
    }
    private static string ReadBounded(TextReader reader)
    {
        var result = Task.Run(() => reader.ReadLine());
        if (!result.Wait(3000)) throw new Exception("The relay did not return a line.");
        return result.Result;
    }
    private static void Test(bool closeHost)
    {
        string pipe = "EditBuddy.Bridge.Tests." + Guid.NewGuid().ToString("N");
        using (var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
        using (var child = Child(pipe))
        {
            try
            {
                Connected(server);
                var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, true);
                var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                try
                {
                    child.StandardInput.WriteLine("{\"id\":\"roundtrip\",\"method\":\"get_state\",\"params\":{}}"); child.StandardInput.Flush();
                    Check(ReadBounded(reader).Contains("roundtrip"), "Input pump did not forward a request.");
                    writer.WriteLine("{\"id\":\"roundtrip\",\"result\":{},\"error\":null}");
                    Check(ReadBounded(child.StandardOutput).Contains("roundtrip"), "Output pump did not forward the response.");
                    if (!closeHost)
                    {
                        child.StandardInput.Close();
                        Check(child.WaitForExit(3000), "Bridge retained its executable after SSH stdin EOF while host stayed open.");
                        Check(child.ExitCode == 0, "stdin EOF did not exit cleanly: " + child.StandardError.ReadToEnd());
                        Check(ReadBounded(reader) == null, "stdin EOF did not close the host connection.");
                    }
                }
                finally { try { writer.Dispose(); } catch (IOException) { } reader.Dispose(); }
                if (closeHost)
                {
                    server.Dispose(); // Deliberately leave child.StandardInput open.
                    Check(child.WaitForExit(3000), "Bridge stayed alive after host EOF with SSH stdin still open.");
                    Check(child.ExitCode == 0, "Host EOF did not exit cleanly: " + child.StandardError.ReadToEnd());
                }
            }
            finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); } }
        }
    }
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--child")
        {
            try
            {
                using (var channel = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous))
                { channel.Connect(3000); PortalBridgeMain.Relay(channel, Console.In, Console.Out); }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        try { Test(true); Test(false); Console.WriteLine("PASS: " + assertions + " bridge subprocess assertions."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
