using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.RegularExpressions;

namespace EditBuddy
{
    // Pure EDL text generation/comparison. These types never operate MAGIX.
    public sealed class PortalEdlClip
    {
        public string ClipId { get; set; }
        public string MediaPath { get; set; }
        public int TimelineInFrame { get; set; }
        public int SourceInFrame { get; set; }
        public int SourceOutFrame { get; set; }
        public int Track { get; set; }
    }
    public sealed class PortalEdlObject
    {
        public string source { get; set; }
        public int track { get; set; }
        public long play_in { get; set; }
        public long play_out { get; set; }
        public long record_in { get; set; }
        public long record_out { get; set; }
        internal string Canonical()
        { return String.Join("|", new[] { source.ToUpperInvariant(), track.ToString(CultureInfo.InvariantCulture), play_in.ToString(CultureInfo.InvariantCulture), play_out.ToString(CultureInfo.InvariantCulture), record_in.ToString(CultureInfo.InvariantCulture), record_out.ToString(CultureInfo.InvariantCulture) }); }
    }
    public sealed class PortalEdlDocument
    {
        public string version { get; set; }
        public int sample_rate { get; set; }
        public List<string> sources { get; set; }
        public List<PortalEdlObject> objects { get; set; }
    }
    public sealed class PortalEdlComparison
    {
        public bool matches_expected { get; set; }
        public List<string> mismatches { get; set; }
        public PortalEdlDocument expected { get; set; }
        public PortalEdlDocument observed { get; set; }
        // Text equality can never establish which application created the text.
        public bool native_application_verified { get { return false; } }
    }
    public sealed class PortalEdlNativeExportClaim
    {
        public string origin { get; set; }
        public string operation_receipt_id { get; set; }
        public string export_path { get; set; }
        public string export_sha256 { get; set; }
        public int magix_process_id { get; set; }
    }
    public sealed class PortalEdlRoundTripEvidence
    {
        public PortalEdlComparison comparison { get; set; }
        public string generated_path { get; set; }
        public string exported_path { get; set; }
        public string generated_sha256 { get; set; }
        public string exported_sha256 { get; set; }
        public bool files_unchanged { get; set; }
        public bool native_export_claim_supplied { get; set; }
        public bool eligible_native_evidence { get; set; }
        public PortalEdlNativeExportClaim provenance { get; set; }
        public string limitation { get { return "The caller must substantiate the claim with an actual MAGIX export operation receipt. Text equality alone never proves native editing."; } }
    }

    public static class PortalEdl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity info);
        private static bool SameFile(string first, string second)
        {
            using (var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                FileIdentity x, y;
                if (!GetFileInformationByHandle(a.SafeFileHandle, out x) || !GetFileInformationByHandle(b.SafeFileHandle, out y)) throw new IOException("Unable to establish EDL file identity.");
                return x.Volume == y.Volume && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
            }
        }
        public const int SampleRate = 48000;
        public const int FramesPerSecond = 30;
        private static readonly Regex Header = new Regex(@"^Samplitude EDL File Format Version (1\.[567])$", RegexOptions.CultureInvariant);
        private static readonly Regex SourceRow = new Regex(@"^(\d+)\s+""([^""]+)""$", RegexOptions.CultureInvariant);
        private static readonly Regex ObjectRow = new Regex(@"^(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)(?:\s+.*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex TrackHeader = new Regex(@"^Track \d+:\s*""[^""]*""(?:\s+Solo:\s*[01]\s+Mute:\s*[01])?$", RegexOptions.CultureInvariant);
        public static long FramesToSamples(int frame)
        { if (frame < 0) throw new ArgumentException("Frame positions cannot be negative."); return checked((long)frame * 1600L); }
        private static string FullPath(string value, string root)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Any(c => c > 127 || c == '"' || c == '\r' || c == '\n' || c == '\0') || !Regex.IsMatch(value, @"^[A-Za-z]:[\\/]"))
                throw new ArgumentException("The EDL prototype requires an ASCII absolute Windows drive path.");
            if (value.Split(new[] { '\\', '/' }).Contains("..") || value.IndexOf(':', 2) >= 0)
                throw new ArgumentException("Parent traversal and alternate streams are unsupported.");
            string normalized = Path.GetFullPath(value).TrimEnd('\\');
            if (root != null && !normalized.StartsWith(FullPath(root, null) + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Source media must stay inside the MSI workspace.");
            return normalized;
        }
        public static string Generate(string workspace, IEnumerable<PortalEdlClip> input)
        {
            string root = FullPath(workspace, null);
            var clips = input == null ? new List<PortalEdlClip>() : input.ToList();
            if (clips.Count < 1 || clips.Count > 64 || clips.Any(c => c == null)) throw new ArgumentException("Expected 1–64 video objects.");
            var sources = new List<string>();
            var sourceIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var ends = new Dictionary<int, long>();
            var rows = new List<string>();
            foreach (var clip in clips.OrderBy(c => c.Track).ThenBy(c => c.TimelineInFrame))
            {
                if (clip.ClipId == null || !Regex.IsMatch(clip.ClipId, @"^[A-Za-z0-9_.-]{1,80}$") || !ids.Add(clip.ClipId)) throw new ArgumentException("Invalid or duplicate clip ID.");
                string path = FullPath(clip.MediaPath, root);
                if (!new[] { ".avi", ".mp4", ".mov", ".mxf", ".mts", ".m2ts", ".wmv", ".mxv" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    throw new ArgumentException("This prototype accepts original video references only.");
                if (clip.Track < 1 || clip.Track > 99 || clip.SourceInFrame < 0 || clip.SourceOutFrame <= clip.SourceInFrame || clip.TimelineInFrame < 0)
                    throw new ArgumentException("Invalid track or frame range.");
                long prior; ends.TryGetValue(clip.Track, out prior);
                if (clip.TimelineInFrame < prior) throw new ArgumentException("Overlapping objects are outside this prototype.");
                long duration = (long)clip.SourceOutFrame - clip.SourceInFrame;
                ends[clip.Track] = clip.TimelineInFrame + duration;
                int sourceId;
                if (!sourceIds.TryGetValue(path, out sourceId)) { sources.Add(path); sourceId = sources.Count; sourceIds.Add(path, sourceId); }
                rows.Add(String.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4} {5} 0.0 0 0 0 0 \"*default\" 0 0 \"*default\" \"{6}\"",
                    sourceId, clip.Track, FramesToSamples(clip.TimelineInFrame), checked(ends[clip.Track] * 1600L), FramesToSamples(clip.SourceInFrame), FramesToSamples(clip.SourceOutFrame), clip.ClipId));
            }
            var lines = new List<string> { "Samplitude EDL File Format Version 1.5", "Title: \"ED-IT Buddy synthetic demo\"", "Sample Rate: 48000", "Output Channels: 2", "", "Source Table Entries: " + sources.Count };
            for (int i = 0; i < sources.Count; i++) lines.Add((i + 1).ToString(CultureInfo.InvariantCulture) + " \"" + sources[i] + "\"");
            foreach (int track in ends.Keys.OrderBy(i => i)) { lines.Add(""); lines.Add("Track " + track + ": \"Buddy video " + track + "\" Solo: 0 Mute: 0"); }
            lines.Add(""); lines.Add("#Source Track Play-In Play-Out Record-In Record-Out Vol(dB) MT LK FadeIn % CurveType FadeOut % CurveType Name");
            lines.AddRange(rows);
            return String.Join("\r\n", lines) + "\r\n";
        }
        public static PortalEdlDocument Parse(string content)
        {
            if (content == null || content.Length > 1024 * 1024) throw new ArgumentException("Missing or oversized EDL text.");
            string version = null; int rate = 0, remaining = -1;
            var sources = new Dictionary<int, string>();
            var rows = new List<PortalEdlObject>();
            foreach (string raw in content.TrimStart('\uFEFF').Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                Match match;
                if (version == null) { match = Header.Match(line); if (!match.Success) throw new FormatException("Unknown Samplitude EDL header."); version = match.Groups[1].Value; continue; }
                if (remaining > 0)
                {
                    match = SourceRow.Match(line); if (!match.Success) throw new FormatException("Malformed source table row.");
                    int id = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (id < 1 || sources.ContainsKey(id)) throw new FormatException("Duplicate or invalid source ID.");
                    sources.Add(id, FullPath(match.Groups[2].Value, null)); remaining--; continue;
                }
                if (line.StartsWith("Sample Rate:", StringComparison.Ordinal)) { if (rate != 0) throw new FormatException("Duplicate sample rate."); rate = Int32.Parse(line.Substring(12).Trim(), CultureInfo.InvariantCulture); if (rate <= 0) throw new FormatException("Invalid sample rate."); continue; }
                if (line.StartsWith("Source Table Entries:", StringComparison.Ordinal)) { if (remaining != -1) throw new FormatException("Duplicate source table."); remaining = Int32.Parse(line.Substring(21).Trim(), CultureInfo.InvariantCulture); if (remaining < 1 || remaining > 64) throw new FormatException("Source count outside prototype bounds."); continue; }
                if (Regex.IsMatch(line, @"^Title:\s*""[^""]*""$") || Regex.IsMatch(line, @"^Output Channels:\s*\d+$") || TrackHeader.IsMatch(line)) continue;
                match = ObjectRow.Match(line); if (!match.Success) throw new FormatException("Unrecognized EDL record; comparison refused: " + line.Substring(0, Math.Min(120, line.Length)));
                int source = Int32.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                if (!sources.ContainsKey(source)) throw new FormatException("Object references an unknown source.");
                var row = new PortalEdlObject { source = sources[source], track = Int32.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), play_in = Int64.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), play_out = Int64.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture), record_in = Int64.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture), record_out = Int64.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) };
                if (row.track < 1 || row.play_out <= row.play_in || row.record_out <= row.record_in || row.play_out - row.play_in != row.record_out - row.record_in) throw new FormatException("Invalid object sample ranges.");
                rows.Add(row); if (rows.Count > 64) throw new FormatException("Too many objects.");
            }
            if (version == null || rate <= 0 || remaining != 0 || rows.Count == 0) throw new FormatException("Incomplete EDL.");
            return new PortalEdlDocument { version = version, sample_rate = rate, sources = sources.Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), objects = rows.OrderBy(x => x.track).ThenBy(x => x.play_in).ThenBy(x => x.source, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.record_in).ToList() };
        }
        public static PortalEdlComparison Compare(string generated, string exported)
        {
            var expected = Parse(generated); var observed = Parse(exported); var errors = new List<string>();
            if (expected.sample_rate != observed.sample_rate) errors.Add("sample_rate");
            if (!expected.sources.SequenceEqual(observed.sources, StringComparer.OrdinalIgnoreCase)) errors.Add("sources");
            if (!expected.objects.Select(x => x.Canonical()).SequenceEqual(observed.objects.Select(x => x.Canonical()))) errors.Add("objects");
            return new PortalEdlComparison { matches_expected = errors.Count == 0, mismatches = errors, expected = expected, observed = observed };
        }
        public static string Sha256(string path)
        { using (var input = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant(); }
        public static PortalEdlRoundTripEvidence ValidateRoundTrip(string generatedPath, string exportedPath, PortalEdlNativeExportClaim provenance)
        {
            string generated = Path.GetFullPath(generatedPath), exported = Path.GetFullPath(exportedPath);
            if (String.Equals(generated, exported, StringComparison.OrdinalIgnoreCase) || SameFile(generated, exported)) throw new ArgumentException("Native export evidence must be distinct from the generated input.");
            if ((File.GetAttributes(generated) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(exported) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Reparse-point artifacts are not accepted.");
            string beforeGenerated = Sha256(generated), beforeExported = Sha256(exported);
            var comparison = Compare(File.ReadAllText(generated), File.ReadAllText(exported));
            bool claim = provenance != null && provenance.origin == "magix_native_export" && provenance.magix_process_id > 0 && !String.IsNullOrWhiteSpace(provenance.operation_receipt_id)
                && !String.IsNullOrWhiteSpace(provenance.export_path) && String.Equals(Path.GetFullPath(provenance.export_path), exported, StringComparison.OrdinalIgnoreCase)
                && String.Equals(provenance.export_sha256, beforeExported, StringComparison.OrdinalIgnoreCase);
            bool unchanged = beforeGenerated == Sha256(generated) && beforeExported == Sha256(exported);
            return new PortalEdlRoundTripEvidence { comparison = comparison, generated_path = generated, exported_path = exported,
                generated_sha256 = beforeGenerated, exported_sha256 = beforeExported, files_unchanged = unchanged, provenance = provenance,
                native_export_claim_supplied = claim, eligible_native_evidence = comparison.matches_expected && unchanged && claim };
        }
    }
}
