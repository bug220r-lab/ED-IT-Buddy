using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace EditBuddy
{
    internal static class PortalBridgeMain
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                if(args.Length!=0 || !String.Equals(Environment.MachineName,"MSI",StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This fixed bridge only runs on MSI and accepts no command-line actions.");
                string state=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ED-IT Buddy","portal","state","endpoint.json");
                if(!File.Exists(state)) throw new InvalidOperationException("The MSI Buddy desktop host is not running.");
                var json=new JavaScriptSerializer(); var e=json.DeserializeObject(File.ReadAllText(state)) as Dictionary<string,object>;
                string sid=WindowsIdentity.GetCurrent().User.Value;
                if(e==null || Convert.ToString(e["user_sid"])!=sid || Convert.ToString(e["host_name"])!="MSI")throw new InvalidOperationException("Host identity mismatch.");
                int session=Convert.ToInt32(e["session_id"]); string pipe=PortalPipeServer.PipeNameForSession(sid,session);
                if(session==0 || pipe!=Convert.ToString(e["pipe_name"]))throw new InvalidOperationException("Host is not the expected interactive endpoint.");
                using(Process p=Process.GetProcessById(Convert.ToInt32(e["process_id"]))) {
                    string expected=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ED-IT Buddy.Portal.exe");
                    if(p.SessionId!=session || p.StartTime.ToUniversalTime().Ticks!=Convert.ToInt64(e["process_start_ticks"]) || !String.Equals(p.MainModule.FileName,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Desktop host changed. Reconnect after restarting Buddy.");
                }
                using(var input = OpenSshStandardInput())
                using(var channel=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification))
                {
                    channel.Connect(5000);
                    Relay(channel, input, Console.Out);
                }
                return 0;
            }
            catch(Exception ex){Console.Error.WriteLine("Buddy bridge: "+ex.Message);return 1;}
        }
        // On MSI's Windows OpenSSH, Framework Console.In/ConsoleStream reads the
        // first message but stalls on subsequent writes and EOF. A FileStream
        // over the inherited pipe handle was verified with delayed multi-line
        // writes and EOF. Do not take ownership of the process standard handle.
        private static StreamReader OpenSshStandardInput()
        {
            var handle = new SafeFileHandle(GetStdHandle(-10), false);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("SSH standard input is unavailable."); }
            return new StreamReader(new FileStream(handle, FileAccess.Read, 4096, false), new UTF8Encoding(false, true), false, 4096);
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);
        // Both reads are background operations. When either the host pipe or SSH
        // stdin ends, return from Main even if the other input remains open.
        // In particular, never leave a bridge executable locked after its host exits.
        internal static void Relay(Stream channel, TextReader input, TextWriter output)
        {
            var reader = new StreamReader(channel, new UTF8Encoding(false), false, 4096, true);
            var writer = new StreamWriter(channel, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            try
            {
                Task incoming = Task.Run(delegate {
                    string request; while ((request = ReadLine(input)) != null) writer.WriteLine(request);
                });
                Task outgoing = Task.Run(delegate {
                    string response; while ((response = ReadLine(reader)) != null) { output.WriteLine(response); output.Flush(); }
                });
                Task[] pumps = { incoming, outgoing };
                int completed = Task.WaitAny(pumps);
                pumps[completed].GetAwaiter().GetResult();
                // The other pump may receive an expected disposal error; observe
                // it without waiting for a still-open Console.In stream.
                pumps[1 - completed].ContinueWith(t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            finally
            {
                channel.Dispose();
                try { reader.Dispose(); } catch { }
                try { writer.Dispose(); } catch { }
            }
        }
        private static string ReadLine(TextReader reader)
        {
            StringBuilder b=new StringBuilder(); int value;
            while((value=reader.Read())!=-1) { if(value=='\n')return b.ToString().TrimEnd('\r'); if(b.Length>=1048576)throw new InvalidDataException("Protocol line exceeds 1 MiB.");b.Append((char)value); }
            return b.Length==0?null:b.ToString();
        }
    }
}
