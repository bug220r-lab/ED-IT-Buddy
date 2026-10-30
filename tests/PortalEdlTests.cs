using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EditBuddy;

internal static class PortalEdlTests
{
    private static int count;
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; }
    private static void Throws(Action action, string label) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, label); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
    public static int Main(string[] args)
    {
        try
        {
            string root = @"C:\BuddyDemo";
            var clips = new[] {
                new PortalEdlClip { ClipId="A", MediaPath=root+@"\Clip-A.avi", TimelineInFrame=0, SourceInFrame=0, SourceOutFrame=60, Track=1 },
                new PortalEdlClip { ClipId="B", MediaPath=root+@"\Clip-B.avi", TimelineInFrame=60, SourceInFrame=30, SourceOutFrame=120, Track=1 }
            };
            string text = PortalEdl.Generate(root, clips);
            Check(text.Contains("1 1 0 96000 0 96000 0.0"), "A golden samples");
            Check(text.Contains("2 1 96000 240000 48000 192000 0.0"), "B source offset versus timeline offset");
            Check(PortalEdl.FramesToSamples(150)==240000, "exact 30fps sample conversion");
            string native = text.Replace("Version 1.5", "Version 1.7").Replace("1 \"C:", "7 \"C:").Replace("2 \"C:", "8 \"C:").Replace("\r\n1 1 ", "\r\n7 1 ").Replace("\r\n2 1 ", "\r\n8 1 ");
            Check(PortalEdl.Compare(text,native).matches_expected, "native source IDs may be renumbered");
            Check(!PortalEdl.Compare(text,native).native_application_verified, "text cannot claim native action");
            Check(!PortalEdl.Compare(text,native.Replace("48000 192000", "0 144000")).matches_expected, "wrong source boundaries fail");
            Check(!PortalEdl.Compare(text,native.Replace("8 1 96000", "8 2 96000")).matches_expected, "wrong track fails");
            Check(!PortalEdl.Compare(text,native.Replace("Clip-B.avi", "Other-B.avi")).matches_expected, "wrong source path fails");
            Throws(()=>PortalEdl.Parse(text+"Unknown record\r\n"), "unknown records fail closed");
            Throws(()=>PortalEdl.Parse(text.Replace("Source Table Entries: 2", "Source Table Entries: 3")), "truncated source table fails");
            Throws(()=>PortalEdl.Parse(text.Replace("48000 192000", "48000 192001")), "mismatched duration fails");
            Throws(()=>PortalEdl.Generate(root,new[]{new PortalEdlClip {ClipId="A",MediaPath=@"C:\Other\Clip-A.avi",SourceOutFrame=60,Track=1}}), "path escape refused");
            string directory = Path.GetFullPath(args[0]); Directory.CreateDirectory(directory);
            string generated=Path.Combine(directory,"generated.edl"), exported=Path.Combine(directory,"claimed-export.edl");
            File.WriteAllText(generated,text); File.WriteAllText(exported,native);
            Throws(()=>PortalEdl.ValidateRoundTrip(generated,generated,null), "same input file refused");
            var result=PortalEdl.ValidateRoundTrip(generated,exported,null);
            Check(result.comparison.matches_expected && !result.eligible_native_evidence, "distinct matching text still needs native provenance");
            var claim=new PortalEdlNativeExportClaim {origin="magix_native_export",operation_receipt_id="test-only-not-real-native-export",export_path=exported,export_sha256=PortalEdl.Sha256(exported),magix_process_id=1};
            Check(PortalEdl.ValidateRoundTrip(generated,exported,claim).eligible_native_evidence,"explicit bound claim can be consumed by trusted adapter");
            claim.export_sha256="wrong";
            Check(!PortalEdl.ValidateRoundTrip(generated,exported,claim).eligible_native_evidence,"wrong artifact hash refused");
            string alias=Path.Combine(directory,"input-hardlink.edl");
            if (!CreateHardLink(alias,generated,IntPtr.Zero)) throw new Exception("Cannot create test hardlink: "+Marshal.GetLastWin32Error());
            Throws(()=>PortalEdl.ValidateRoundTrip(generated,alias,null),"hardlink to generated input refused");
            Console.WriteLine("PASS "+count+" EDL checks"); return 0;
        }
        catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
}
