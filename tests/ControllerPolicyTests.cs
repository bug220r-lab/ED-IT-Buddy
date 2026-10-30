using System;
using System.Collections.Generic;
using EditBuddy;

internal static class ControllerPolicyTests
{
    private static int failures;
    private static int passed;
    private static void Test(string name,Action body)
    {
        try {body();passed++;Console.WriteLine("PASS "+name);}
        catch(Exception ex){failures++;Console.WriteLine("FAIL "+name+": "+ex.Message);}
    }
    private static void Check(bool condition,string message) {if(!condition)throw new Exception(message);}
    private static WindowRecord Editor(long hwnd=10,int pid=20)
    {return new WindowRecord {Handle=hwnd,ProcessId=pid,ClassName=EditorWindowPolicy.EditorClass,Visible=true,Enabled=true,RootOwner=hwnd};}
    public static int Main()
    {
        Test("Only advertised actions are executable",delegate {
            Check(ActionPolicy.IsSupported("focus_magix")&&ActionPolicy.IsSupported("inspect_controls"),"Known actions must be available.");
            foreach(string value in new[]{null,"","show_effects","show_import","delete","Focus_Magix","focus_magix;calc.exe"})
                Check(!ActionPolicy.IsSupported(value),"Unexpected action accepted: "+value);
        });
        Test("Splash and modal windows cannot become editor targets",delegate {
            var windows=new[]{new WindowRecord {Handle=1,ProcessId=20,ClassName="#32770",Visible=true,Enabled=true}};
            Check(!EditorWindowPolicy.Select(windows,20).Connected,"A dialog was mistaken for the editor.");
        });
        Test("Valid editor can connect",delegate {
            var result=EditorWindowPolicy.Select(new[]{Editor()},20);
            Check(result.Connected&&!result.Blocked&&result.Handle==10,"Valid editor rejected.");
        });
        Test("Different process, invisible and zero-handle windows are rejected",delegate {
            var hidden=Editor();hidden.Visible=false;var zero=Editor(0);var wrong=Editor(11,21);
            Check(!EditorWindowPolicy.Select(new[]{hidden,zero,wrong},20).Connected,"Invalid target accepted.");
            Check(!EditorWindowPolicy.Select(new[]{Editor(10,0)},0).Connected,"Zero process accepted.");
        });
        Test("Ambiguous editor windows block connection",delegate {
            var result=EditorWindowPolicy.Select(new[]{Editor(),Editor(11)},20);
            Check(!result.Connected&&result.Blocked,"An arbitrary editor was chosen.");
        });
        Test("Disabled owner blocks even with enabled child controls",delegate {
            var editor=Editor();editor.Enabled=false;
            Check(EditorWindowPolicy.Select(new[]{editor},20).Blocked,"Disabled owner was ignored.");
        });
        Test("Related secondary window blocks; unrelated windows do not",delegate {
            var related=new WindowRecord {Handle=30,ProcessId=20,ClassName="#32770",Visible=true,Enabled=true,Owner=10,RootOwner=10};
            Check(EditorWindowPolicy.Select(new[]{Editor(),related},20).Blocked,"Related dialog ignored.");
            related.RootOwner=99;
            Check(!EditorWindowPolicy.Select(new[]{Editor(),related},20).Blocked,"Unrelated dialog blocked editor.");
            related.RootOwner=10;related.ProcessId=30;
            Check(!EditorWindowPolicy.Select(new[]{Editor(),related},20).Blocked,"Other process blocked editor.");
        });
        Test("Empty observations do not claim a connection",delegate {
            Check(!EditorWindowPolicy.Select(null,20).Connected,"Null snapshot connected.");
            Check(!EditorWindowPolicy.Select(new WindowRecord[0],20).Connected,"Empty snapshot connected.");
        });
        Test("Observed effects tab is distinguished from effects menu",delegate {
            var controls=new[]{new ControlRecord {Type="Button",Name="Effects",Id="5821",Enabled=true,Patterns=new[]{"Invoke"}}};
            Check(!ControlEvidence.Summarize(controls).Contains("Effects: accessible"),"Menu misidentified as tab.");
            controls[0].Id="10915";
            string result=ControlEvidence.Summarize(controls);
            Check(result.Contains("Effects: accessible")&&result.Contains("has not been validated"),"Evidence overstated or omitted.");
        });
        Test("Disabled and duplicate buttons do not imply usable actions",delegate {
            var effects=new ControlRecord {Type="Button",Name="Effects",Id="10915",Enabled=false,Patterns=new[]{"Invoke"}};
            Check(ControlEvidence.Summarize(new[]{effects}).Contains("disabled or offscreen"),"Disabled state lost.");
            Check(ControlEvidence.Summarize(new[]{effects,effects}).Contains("ambiguous"),"Duplicate target not identified.");
        });
        Test("Custom timecode limitation is explicit",delegate {
            Check(ControlEvidence.Summarize(new[]{new ControlRecord {Type="Custom",Name="timecode",Patterns=new string[0]}}).Contains("seeking remains manual"),"Custom timecode was treated as seekable.");
        });
        Console.WriteLine("Results: "+passed+" passed, "+failures+" failed. No Windows APIs were called.");
        return failures==0?0:1;
    }
}
