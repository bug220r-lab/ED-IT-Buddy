using System;
using System.Globalization;
using System.Web.Script.Serialization;
namespace EditBuddy
{
    internal static class ProbeMain
    {
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                long hwnd;int pid;
                if(args.Length!=2 || !Int64.TryParse(args[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out hwnd) || !Int32.TryParse(args[1],out pid)) return 2;
                Console.OutputEncoding=System.Text.Encoding.UTF8;
                Console.Write(new JavaScriptSerializer().Serialize(AccessibilityProbe.Read(new IntPtr(hwnd),pid)));
                return 0;
            }
            catch(Exception ex){Console.Error.Write(ex.Message);return 1;}
        }
    }
}
