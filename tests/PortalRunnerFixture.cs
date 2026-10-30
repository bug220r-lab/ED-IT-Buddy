using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Protocol fixture only: its simulated input is a text file, never a Windows UI call.
internal static class PortalRunnerFixture
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Emit(object value) { Console.WriteLine(Json.Serialize(value)); Console.Out.Flush(); }
    private static void Result(string status, bool input) { Emit(new { kind="result", result=new { status=status, input_sent=input, message="Synthetic protocol fixture." } }); }
    public static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var request = Json.Deserialize<Dictionary<string,object>>(Console.ReadLine());
            var args = (Dictionary<string,object>)request["args"];
            string workspace = (string)request["workspace"];
            string mode = Path.GetFileNameWithoutExtension((string)args["project_path"]);
            using (var process = Process.GetCurrentProcess()) File.WriteAllText(Path.Combine(workspace,"worker-identity.txt"),process.Id+"\n"+process.StartTime.ToUniversalTime().Ticks);
            if(mode=="forged") { Result("verified_success",true); return 0; }
            if(mode=="after-final") Result("verified_success",false);
            Emit(new {kind="before_input"});
            string permit=Console.ReadLine();
            if(permit!="permit_input") { Result("failed_before_input",false); return 0; }
            if(!File.Exists(Path.Combine(workspace,"intent.txt"))) File.WriteAllText(Path.Combine(workspace,"missing-intent.txt"),"Permit arrived before durable intent.");
            File.WriteAllText(Path.Combine(workspace,"input-applied.txt"),"Synthetic input only.");
            if(mode=="duplicate") { Emit(new{kind="before_input"}); Thread.Sleep(30000); return 0; }
            if(mode=="hang") { Thread.Sleep(30000); return 0; }
            Result("verified_success",true); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
