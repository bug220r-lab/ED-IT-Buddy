using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EditBuddy;

internal static class PortalRunnerProtocolTests
{
    private static int assertions;
    private static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
    private static PortalStep Step(string workspace,string mode)
    { return new PortalStep{action="save_project",args=new Dictionary<string,object>{{"project_path",Path.Combine(workspace,mode+".MVP")}}}; }
    private static void Mark(string workspace)
    {
        byte[] content=Encoding.UTF8.GetBytes("Durable intent before permit.");
        using(var file=new FileStream(Path.Combine(workspace,"intent.txt"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)){file.Write(content,0,content.Length);file.Flush(true);}
    }
    private static bool WaitForFile(string path,int milliseconds)
    { var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<milliseconds){if(File.Exists(path))return true;Thread.Sleep(10);}return false; }
    private static bool WorkerExited(string workspace)
    {
        string[] identity=File.ReadAllLines(Path.Combine(workspace,"worker-identity.txt"));
        try{using(var process=Process.GetProcessById(Int32.Parse(identity[0]))){if(process.StartTime.ToUniversalTime().Ticks!=Int64.Parse(identity[1]))return true;return process.WaitForExit(3000);}}
        catch(ArgumentException){return true;}
    }
    private static PortalActionResult Execute(string fixture,string root,string mode,Func<bool> cancelled,Action marker)
    {
        Directory.CreateDirectory(root);
        return new MagixEditingRunner(fixture).Execute(Step(root,mode),new PortalExecutionContext(root,"fixture",0,cancelled,marker));
    }
    public static int Main(string[] args)
    {
        if(args.Length==3 && args[0]=="--abrupt-parent")
        {
            string fixture=args[1],root=args[2];Directory.CreateDirectory(root);
            Task.Run(()=>Execute(fixture,root,"hang",()=>false,()=>Mark(root)));
            if(!WaitForFile(Path.Combine(root,"input-applied.txt"),5000))return 2;
            Environment.Exit(17); // Deliberately skip managed using/finally; OS closes the worker JobObject.
            return 3;
        }
        try
        {
            string fixture=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
            string success=Path.Combine(root,"success");
            PortalActionResult ok=Execute(fixture,success,"success",()=>false,()=>Mark(success));
            Check(ok.status=="verified_success" && ok.input_sent,"The permitted protocol fixture did not complete.");
            Check(File.Exists(Path.Combine(success,"input-applied.txt")) && !File.Exists(Path.Combine(success,"missing-intent.txt")),"Input preceded its durable intent.");
            Check(WorkerExited(success),"Completed fixture remained running.");

            string denied=Path.Combine(root,"marker-failure");
            PortalActionResult failure=Execute(fixture,denied,"success",()=>false,()=>{throw new IOException("Synthetic receipt write failure.");});
            Check(failure.status=="failed_before_input" && !File.Exists(Path.Combine(denied,"input-applied.txt")),"A failed intent write granted native input.");
            Check(WorkerExited(denied),"Denied worker remained running.");

            string cancelled=Path.Combine(root,"cancel-before-permit");int stop=0;
            PortalActionResult before=Execute(fixture,cancelled,"success",()=>Interlocked.CompareExchange(ref stop,0,0)!=0,()=>{Mark(cancelled);Interlocked.Exchange(ref stop,1);});
            Check(before.status!="verified_success" && !File.Exists(Path.Combine(cancelled,"input-applied.txt")),"Cancellation during intent persistence still granted input.");
            Check(WorkerExited(cancelled),"Cancelled-before-permit fixture remained running.");

            string after=Path.Combine(root,"cancel-after-input");int laterStop=0;
            Task<PortalActionResult> pending=Task.Run(()=>Execute(fixture,after,"hang",()=>Interlocked.CompareExchange(ref laterStop,0,0)!=0,()=>Mark(after)));
            Check(WaitForFile(Path.Combine(after,"input-applied.txt"),3000),"Hanging fixture never received its permit.");
            Interlocked.Exchange(ref laterStop,1);Check(pending.Wait(5000),"Stop did not bound the worker lifetime.");
            Check(pending.Result.status=="uncertain_after_input" && pending.Result.input_sent,"Stop after input was not classified uncertain.");
            Check(WorkerExited(after),"Stopped worker remained running.");

            foreach(string mode in new[]{"duplicate","forged","after-final"})
            {
                string folder=Path.Combine(root,mode);PortalActionResult result=Execute(fixture,folder,mode,()=>false,()=>Mark(folder));
                Check(result.status!="verified_success","Invalid worker protocol was accepted: "+mode);
                if(mode=="after-final")Check(!File.Exists(Path.Combine(folder,"input-applied.txt")),"Input was granted after the worker's final result.");
                Check(WorkerExited(folder),"Invalid-protocol worker remained running: "+mode);
            }
            string crashed=Path.Combine(root,"abrupt-parent");
            using(var owner=Process.Start(new ProcessStartInfo{FileName=Assembly.GetExecutingAssembly().Location,
                Arguments="--abrupt-parent \""+fixture+"\" \""+crashed+"\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
            {
                Check(owner.WaitForExit(8000) && owner.ExitCode==17,"Abrupt parent fixture did not exit as intended.");
                Check(File.Exists(Path.Combine(crashed,"input-applied.txt")),"The parent exited before the input-phase test.");
                Check(WorkerExited(crashed),"Owned worker survived abrupt parent termination.");
            }
            Console.WriteLine("PASS: "+assertions+" runner protocol/lifetime assertions. No Windows UI or MSI access.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
