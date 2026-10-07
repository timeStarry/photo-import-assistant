using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PhotoImportV2
{
    public class CardRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool AutoImport { get; set; }
        public string DeleteMode { get; set; }
        public long Capacity { get; set; }
        public string Label { get; set; }
        public string LegacySerial { get; set; }
        public string LegacyGuid { get; set; }
        public bool NeedsBinding { get; set; }
    }
    public class AppState
    {
        public string Theme { get; set; }
        public List<DestinationRecord> Destinations { get; set; }
        public string DestinationFailure { get; set; }
        public List<string> ExcludedExtensions { get; set; }
        public bool? PromptForNewCards { get; set; }
        public bool? StartAtLogin { get; set; }
        public int Version { get; set; }
        public List<CardRecord> Cards { get; set; }
        public AppState() { Version = 2; Cards = new List<CardRecord>(); }
    }
    public class DestinationRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool Enabled { get; set; }
        public DestinationRecord() { Enabled = true; }
    }
    public class CardMarker
    {
        public int Version { get; set; }
        public string CardId { get; set; }
        public string CreatedUtc { get; set; }
    }
    public class MediaItem
    {
        public string SourcePath { get; set; }
        public string RelativePath { get; set; }
        public long Length { get; set; }
        public long WriteTicks { get; set; }
    }
    public class DiskSnapshot
    {
        public string Root { get; set; }
        public string Label { get; set; }
        public string Serial { get; set; }
        public string MarkerId { get; set; }
        public string MarkerError { get; set; }
        public long Capacity { get; set; }
        public long? FreeSpace { get; set; }
        public string FileSystem { get; set; }
        public string Error { get; set; }
        public List<MediaItem> Media { get; set; }
        public CandidatePlan Plan { get; set; }
        public string ComparisonError { get; set; }
        public DiskSnapshot() { Media = new List<MediaItem>(); }
        public string Key { get { return Root + "|" + Serial + "|" + Capacity; } }
    }
    public class TransferReceipt
    {
        public string SourcePath { get; set; }
        public string SourceRoot { get; set; }
        public string CardId { get; set; }
        public string Serial { get; set; }
        public long Capacity { get; set; }
        public long Length { get; set; }
        public long WriteTicks { get; set; }
        public string DestinationPath { get; set; }
        public string DestinationKind { get; set; }
        public string Sha256 { get; set; }
        public string VerifiedUtc { get; set; }
    }
    public class WorkRequest
    {
        public string PreviousCardId { get; set; }
        public string Operation { get; set; }
        public string SourceRoot { get; set; }
        public string CardId { get; set; }
        public string ExpectedSerial { get; set; }
        public long ExpectedCapacity { get; set; }
        public MediaItem File { get; set; }
        public string DestinationRoot { get; set; }
        public string DestinationKind { get; set; }
        public TransferReceipt Receipt { get; set; }
        public List<MediaItem> CandidateFiles { get; set; }
        public List<DestinationRecord> ComparisonTargets { get; set; }
        public List<string> ExcludedExtensions { get; set; }
        public string ProgressPath { get; set; }
    }
    public class WorkResult
    {
        public bool Success { get; set; }
        public bool Deleted { get; set; }
        public bool ReusedExisting { get; set; }
        public string Error { get; set; }
        public TransferReceipt Receipt { get; set; }
        public CandidatePlan Plan { get; set; }
    }
    public class CandidatePlan
    {
        public List<MediaItem> Candidates { get; set; }
        public int ExistingCount { get; set; }
        public int ExcludedCount { get; set; }
        public List<string> Warnings { get; set; }
        public CandidatePlan() { Candidates = new List<MediaItem>(); Warnings = new List<string>(); }
    }
    public class CandidateProgress
    {
        public string Message { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
    }
    public static class JsonFile
    {
        public static T Read<T>(string path)
        {
            return new JavaScriptSerializer { MaxJsonLength = 32000000 }.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }
        public static void Write<T>(string path, T value)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = 32000000 }.Serialize(value));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (System.IO.File.Exists(path)) System.IO.File.Replace(temp, path, path + ".bak");
                else System.IO.File.Move(temp, path);
            }
            finally { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
        }
    }
}
