using System;
using System.Collections.Generic;
using System.IO;

namespace PhotoImportV2
{
    public sealed class RemoteObject
    {
        public string Path { get; set; }
        public long Length { get; set; }
        public string ETag { get; set; }
    }
    // Relative object names use '/', stay beneath the configured root and never
    // include credentials. Implementations must disable cross-origin redirects.
    public interface IRemoteStore
    {
        IEnumerable<RemoteObject> List();
        RemoteObject Stat(string relativePath);
        Stream OpenRead(string relativePath);
        // CREATE_NEW semantics: false on an occupied name, never overwrite.
        // Upload progress is cumulative bytes consumed from input.
        bool UploadNew(string relativePath, Stream input, long length, Action<long> progress);
        WorkResult Probe(bool writeTest);
    }
    public static class RemoteStores
    {
        public static IRemoteStore Create(DestinationRecord destination, int idleTimeoutSeconds = 180)
        {
            destination = StorageSettings.Normalize(destination);
            int idle = idleTimeoutSeconds > 0 ? Math.Max(30, Math.Min(1800, idleTimeoutSeconds)) : 180;
            if (destination.Type == "webdav") return new WebDavStore(destination, idle);
            if (destination.Type == "s3") return new S3Store(destination, idle);
            throw new ArgumentException("Not a direct remote storage location.");
        }
    }
}
