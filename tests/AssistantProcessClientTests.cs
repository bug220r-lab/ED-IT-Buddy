using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EditBuddy;

internal static class AssistantProcessClientTests
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.Combine(root, "assistant"));
            File.WriteAllText(Path.Combine(root, "assistant", "client.mjs"), FakeBackend, new UTF8Encoding(false));
            using (AssistantProcessClient client = new AssistantProcessClient(root))
            {
                ManualResetEvent notification = new ManualResetEvent(false);
                client.Notification += delegate(Dictionary<string, object> value) { if (AssistantView.Text(value, "event") == "chat_delta") notification.Set(); };
                object echoed = client.RequestAsync("echo", new { text = "Jude's footage · café 🎸", delay = 5 }).GetAwaiter().GetResult();
                Check(AssistantView.Text(AssistantView.Map(echoed), "text") == "Jude's footage · café 🎸", "UTF-8 request and response");
                Check(notification.WaitOne(2000), "notifications delivered separately from replies");
                Task<object>[] requests = Enumerable.Range(0, 24).Select(delegate(int i) { return client.RequestAsync("echo", new { number = i, delay = (24 - i) * 3 }); }).ToArray();
                Task.WaitAll(requests);
                for (int i = 0; i < requests.Length; i++) Check(AssistantView.Number(AssistantView.Map(requests[i].Result), "number", -1) == i, "out-of-order reply correlation");
                Throws(delegate { client.RequestAsync("fail", new { }).GetAwaiter().GetResult(); }, "fixture failure", "structured errors reach caller");
                Throws(delegate { client.RequestAsync("counted_hang", new { }, 80).GetAwaiter().GetResult(); }, "reply in time", "timeout is bounded");
                Dictionary<string, object> stats = AssistantView.Map(client.RequestAsync("stats", new { }).GetAwaiter().GetResult());
                Check(AssistantView.Number(stats, "hang_count", 0) == 1, "timed-out work is never replayed");
                Task<object> abandoned = client.RequestAsync("counted_hang", new { }, 5000);
                client.Dispose(); Throws(delegate { abandoned.GetAwaiter().GetResult(); }, "closing", "dispose releases pending callers");
            }
            using (AssistantProcessClient client = new AssistantProcessClient(root))
            {
                Throws(delegate { client.RequestAsync("malformed", new { }).GetAwaiter().GetResult(); }, "connection failed", "malformed broker output fails pending request");
            }
            Render(root);
            SearchResultsRecovery(root);
            Console.WriteLine("PASS: " + checks + " assistant protocol/layout checks; fixture " + root); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Render(string root)
    {
        using (AssistantView view = new AssistantView(root))
        {
            Window window = new Window { Content = view, Width = 1160, Height = 760 }; MatrixTheme.Apply(window);
            view.Measure(new Size(1120, 720)); view.Arrange(new Rect(0, 0, 1120, 720)); view.UpdateLayout();
            Check(view.DesiredSize.Width <= 1121 && view.ActualWidth > 0, "manager can lay out offscreen without starting broker");
            RenderTargetBitmap image = new RenderTargetBitmap(1120, 720, 96, 96, PixelFormats.Pbgra32); image.Render(view);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream file = File.Create(Path.Combine(root, "manager-preview.png"))) encoder.Save(file);
            window.Content = null; window.Close();
        }
    }

    private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL: " + label); checks++; }
    private static void SearchResultsRecovery(string root)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using (AssistantView view = new AssistantView(root))
        {
            AssistantProcessClient backend = new AssistantProcessClient(root);
            typeof(AssistantView).GetField("client", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(view, backend);
            Action<string, object[]> call = delegate(string name, object[] parameters) {
                Task work = typeof(AssistantView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, parameters) as Task;
                if (work == null) return;
                DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                while (!work.IsCompleted && DateTime.UtcNow < deadline)
                {
                    DispatcherFrame frame = new DispatcherFrame(); DispatcherTimer pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                    pulse.Tick += delegate { pulse.Stop(); frame.Continue = false; }; pulse.Start(); Dispatcher.PushFrame(frame);
                }
                Check(work.IsCompleted, name + " finishes"); work.GetAwaiter().GetResult();
            };
            // Search replies can arrive before an overlapping task poll sees
            // the new job. Retain that selection until its row becomes visible.
            call("SelectReturnedTask", new object[] { new Dictionary<string, object> { { "task_id", "search-fixture" } } });
            call("RefreshTasks", new object[] { false });
            ListBox tasks = (ListBox)typeof(AssistantView).GetField("taskList", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
            ListBox assets = (ListBox)typeof(AssistantView).GetField("assetList", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(view);
            Check(tasks.SelectedItem is AssistantTaskRow && ((AssistantTaskRow)tasks.SelectedItem).Id == "search-fixture", "late task row restores requested search selection");
            Check(assets.Items.Count == 0, "transient result failure leaves results unloaded");
            call("RefreshTasks", new object[] { false });
            Check(assets.Items.Count == 1, "unchanged completed status retries results after transient failure");
            call("RefreshTasks", new object[] { false });
            Dictionary<string, object> stats = AssistantView.Map(backend.RequestAsync("stats", new { }).GetAwaiter().GetResult());
            Check(AssistantView.Number(stats, "result_reads", 0) == 2, "loaded results are not fetched again on every poll");
            tasks.SelectedItem = null;
            call("Bootstrap", new object[0]);
            Check(tasks.SelectedItem is AssistantTaskRow && assets.Items.Count == 1, "reconnect restores existing search results without submitting another job");
        }
        SynchronizationContext.SetSynchronizationContext(null);
    }
    private static void Throws(Action action, string message, string label)
    { try { action(); } catch (Exception error) { Check(error.Message.Contains(message), label + " (" + error.Message + ")"); return; } throw new Exception("FAIL: " + label + " did not throw"); }

    private const string FakeBackend = @"
import readline from 'node:readline';
const input = readline.createInterface({input:process.stdin}); let hangs = 0, resultReads = 0;
input.on('line', line => {
  const value = JSON.parse(line);
  const reply = result => process.stdout.write(JSON.stringify({id:value.id,result,error:null})+'\n');
  if(value.method==='echo') { process.stdout.write(JSON.stringify({event:'chat_delta',delta:'fixture'})+'\n'); setTimeout(()=>reply(value.params),value.params.delay||0); }
  else if(value.method==='fail') process.stdout.write(JSON.stringify({id:value.id,error:{code:'fixture',message:'fixture failure'}})+'\n');
  else if(value.method==='counted_hang') hangs++;
  else if(value.method==='stats') reply({hang_count:hangs,result_reads:resultReads});
  else if(value.method==='tasks'||value.method==='bootstrap') reply({tasks:[{task_id:'search-fixture',kind:'search',status:'completed'}]});
  else if(value.method==='results') { if(++resultReads===1) process.stdout.write(JSON.stringify({id:value.id,error:{message:'Transient result lookup failed'}})+'\n'); else reply({results:[{asset_id:'source-fixture',source_path:'fixture.mp4',start_seconds:1,end_seconds:4}]}); }
  else if(value.method==='malformed') process.stdout.write('not json\n');
});
input.on('close',()=>process.exit(0));
";
}
