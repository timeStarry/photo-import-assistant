using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhotoImportV2
{
    // Self-contained Windows/.NET Framework fixtures. No removable media, NAS, UI,
    // scheduler or caller-supplied paths are used, including for "Remote" receipts.
    public static class TransferEngineTests
    {
        public static string Run()
        {
            var passed = new List<string>();
            Test(passed, "verified copy and receipt", VerifiedCopy);
            Test(passed, "empty file copy", EmptyCopy);
            Test(passed, "existing identical content", ExistingIdentical);
            Test(passed, "same-size collision never overwrites", DifferentContent);
            Test(passed, "hash-name collision and reuse", HashNameCollision);
            Test(passed, "bounded collision exhaustion", CollisionLimit);
            Test(passed, "source length snapshot mutation", LengthMutation);
            Test(passed, "source write-time snapshot mutation", TimeMutation);
            Test(passed, "marker ID and version mismatch", MarkerMismatch);
            Test(passed, "format loss and malformed marker", MarkerLoss);
            Test(passed, "serial and capacity checks", VolumeIdentity);
            Test(passed, "altered destination refuses deletion", AlteredDestination);
            Test(passed, "altered source refuses deletion", AlteredSource);
            Test(passed, "fallback receipt refuses deletion", FallbackReceipt);
            Test(passed, "receipt identity mismatch refuses deletion", ReceiptMismatch);
            Test(passed, "marker loss before delete", DeleteAfterMarkerLoss);
            Test(passed, "successful remote-designated fixture deletion", RemoteDelete);
            Test(passed, "path containment and partial refusal", UnsafePaths);
            Test(passed, "reparse source/destination rejection", ReparsePaths);
            Test(passed, "source and ancestor locks", HandleLocks);
            Test(passed, "atomic publication never replaces", RenameCollision);
            Test(passed, "unknown redirector file IDs are not aliases", UnknownFileIds);
            Test(passed, "copy destination kinds are restricted", DestinationKinds);
            Test(passed, "WebClient classic publication and lowercase remote receipt", ClassicPublication);
            Test(passed, "classic publication racing collision never overwrites", ClassicCollision);
            Test(passed, "classic publication rehashes the final file", ClassicFinalSwap);
            Test(passed, "classic publication rehashes a replaced partial", ClassicPartialSwap);
            Test(passed, "classic failure never cleans a closed-handle pathname", ClassicFailureRetainsPartial);
            Test(passed, "native rename fallback is WebDAV-only", ClassicRestricted);
            Test(passed, "direct copy receipts are reverified before cleanup", DirectReceiptCleanup);
            Test(passed, "WebDAV direct copy commits before verification", DirectCopy);
            Test(passed, "interrupted direct copy retains source without receipt", DirectInterrupted);
            Test(passed, "direct copy rejects same-size tampering after commit", DirectTampered);
            Test(passed, "direct CREATE_NEW and hash collisions never overwrite", DirectCollision);
            Test(passed, "invalid request is caught", InvalidRequest);
            return "PASS: " + passed.Count.ToString(CultureInfo.InvariantCulture) + " transfer fixture tests: " + String.Join("; ", passed.ToArray()) + ".";
        }

        private static void Test(List<string> passed, string name, Action action)
        {
            try { action(); passed.Add(name); }
            catch (Exception error) { throw new InvalidOperationException("Transfer test failed: " + name, error); }
        }

        private static void VerifiedCopy()
        {
            using (var fixture = new Fixture(196731))
            {
                string abandoned = Path.Combine(Path.GetDirectoryName(fixture.Destination), "previous-worker.partial");
                File.WriteAllText(abandoned, "not owned by this worker");
                WorkResult result = Good(TransferEngine.Run(fixture.CopyRequest("Remote")));
                Assert(!result.Deleted && !result.ReusedExisting, "New copy flags are wrong.");
                Assert(File.Exists(fixture.Source), "Copy removed the source.");
                Assert(result.Receipt.DestinationPath == fixture.Destination, "Receipt must identify the actual final path.");
                Assert(result.Receipt.SourceRoot == fixture.CardRoot && result.Receipt.SourcePath == fixture.Source &&
                    result.Receipt.CardId == fixture.CardId && result.Receipt.Length == fixture.Data.Length &&
                    result.Receipt.WriteTicks == fixture.Snapshot.WriteTicks && result.Receipt.DestinationKind == "remote", "Receipt metadata mismatch.");
                Assert(result.Receipt.Serial.Length == 8 && result.Receipt.Capacity > 0, "Receipt must capture observed volume identity.");
                Assert(result.Receipt.Sha256 == Digest(fixture.Data) && Digest(File.ReadAllBytes(result.Receipt.DestinationPath)) == result.Receipt.Sha256, "Verified hash mismatch.");
                Assert(IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, result.Receipt.Serial, result.Receipt.Capacity), "Observed identity is not verifiable.");
                Assert(File.ReadAllText(abandoned) == "not owned by this worker", "Worker touched an unowned partial.");
                Assert(Directory.GetFiles(fixture.DestinationRoot, "*.partial", SearchOption.AllDirectories).Length == 1, "Successful copy left an owned partial.");
            }
        }

        private static void EmptyCopy()
        {
            using (var fixture = new Fixture(0))
            {
                WorkResult result = Good(TransferEngine.Run(fixture.CopyRequest("Local")));
                Assert(new FileInfo(result.Receipt.DestinationPath).Length == 0 && result.Receipt.Sha256 == Digest(new byte[0]), "Empty file verification failed.");
                Assert(File.Exists(fixture.Source), "Copy removed an empty source.");
            }
        }

        private static void ExistingIdentical()
        {
            using (var fixture = new Fixture())
            {
                File.WriteAllBytes(fixture.Destination, fixture.Data);
                DateTime stamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(fixture.Destination, stamp);
                WorkResult result = Good(TransferEngine.Run(fixture.CopyRequest("Remote")));
                Assert(result.ReusedExisting && result.Receipt.DestinationPath == fixture.Destination, "Identical file was not reused.");
                Assert(File.GetLastWriteTimeUtc(fixture.Destination) == stamp && File.Exists(fixture.Source), "Reuse changed an existing file.");
                AssertNoPartials(fixture);
            }
        }

        private static void DifferentContent()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                File.WriteAllBytes(fixture.Destination, other);
                WorkResult result = Good(TransferEngine.Run(fixture.CopyRequest("Remote")));
                Assert(!result.ReusedExisting && result.Receipt.DestinationPath != fixture.Destination &&
                    Path.GetFileName(result.Receipt.DestinationPath).Contains(result.Receipt.Sha256), "Collision did not choose a content-hash filename.");
                Assert(Digest(File.ReadAllBytes(fixture.Destination)) == Digest(other), "Existing different content was overwritten.");
                Assert(Digest(File.ReadAllBytes(result.Receipt.DestinationPath)) == Digest(fixture.Data), "Collision receipt points at wrong content.");
                Assert(File.Exists(fixture.Source), "Collision copy removed the source.");
                AssertNoPartials(fixture);
            }
        }

        private static void HashNameCollision()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                File.WriteAllBytes(fixture.Destination, other);
                string hashPath = fixture.HashDestination(1);
                File.WriteAllBytes(hashPath, other);
                WorkResult first = Good(TransferEngine.Run(fixture.CopyRequest("Remote")));
                Assert(first.Receipt.DestinationPath == fixture.HashDestination(2), "Occupied hash name was not preserved.");
                WorkResult again = Good(TransferEngine.Run(fixture.CopyRequest("Remote")));
                Assert(again.ReusedExisting && again.Receipt.DestinationPath == first.Receipt.DestinationPath, "Hash-suffixed identical file was not reused.");
                Assert(Digest(File.ReadAllBytes(hashPath)) == Digest(other), "Hash-name collision overwrote existing data.");
            }
        }

        private static void CollisionLimit()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                for (int index = 0; index < 32; index++) File.WriteAllBytes(fixture.HashDestination(index), other);
                Refused(TransferEngine.Run(fixture.CopyRequest("Remote")), fixture);
                for (int index = 0; index < 32; index++) Assert(Digest(File.ReadAllBytes(fixture.HashDestination(index))) == Digest(other), "Collision limit changed a file.");
                AssertNoPartials(fixture);
            }
        }

        private static void LengthMutation()
        {
            using (var fixture = new Fixture())
            {
                WorkRequest request = fixture.CopyRequest("Remote");
                File.WriteAllBytes(fixture.Source, new byte[] { 1, 2 });
                Refused(TransferEngine.Run(request), fixture);
                Assert(!File.Exists(fixture.Destination), "Changed source was published.");
            }
        }

        private static void TimeMutation()
        {
            using (var fixture = new Fixture())
            {
                WorkRequest request = fixture.CopyRequest("Remote");
                File.WriteAllBytes(fixture.Source, Changed(fixture.Data));
                File.SetLastWriteTimeUtc(fixture.Source, new DateTime(fixture.Snapshot.WriteTicks, DateTimeKind.Utc).AddSeconds(10));
                Refused(TransferEngine.Run(request), fixture);
                Assert(!File.Exists(fixture.Destination), "Changed source timestamp was ignored.");
            }
        }

        private static void MarkerMismatch()
        {
            using (var fixture = new Fixture())
            {
                fixture.WriteMarker(1, "another-card");
                Assert(!IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, "", 0), "Wrong card accepted.");
                Refused(TransferEngine.Run(fixture.CopyRequest("Remote")), fixture);
                fixture.WriteMarker(2, fixture.CardId);
                Assert(!IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, "", 0), "Unknown marker version accepted.");
                Refused(TransferEngine.Run(fixture.CopyRequest("Remote")), fixture);
            }
        }

        private static void MarkerLoss()
        {
            using (var fixture = new Fixture())
            {
                File.Delete(fixture.Marker);
                Assert(!IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, "", 0), "Missing marker accepted.");
                Refused(TransferEngine.Run(fixture.CopyRequest("Remote")), fixture);
                File.WriteAllText(fixture.Marker, "{not-json");
                Assert(!IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, "", 0), "Malformed marker accepted.");
                Refused(TransferEngine.Run(fixture.CopyRequest("Remote")), fixture);
                File.WriteAllText(fixture.Marker, new string('x', 65537));
                Assert(!IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, "", 0), "Unbounded marker accepted.");
            }
        }

        private static void VolumeIdentity()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("Remote"))).Receipt;
                string wrongSerial = receipt.Serial == "00000000" ? "FFFFFFFF" : "00000000";
                Assert(!IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, wrongSerial, receipt.Capacity), "Wrong volume serial accepted.");
                Assert(!IdentityVerifier.Check(fixture.CardRoot, fixture.CardId, receipt.Serial, receipt.Capacity + 1), "Wrong volume capacity accepted.");
                WorkRequest wrong = fixture.CopyRequest("Remote");
                wrong.ExpectedSerial = wrongSerial;
                Refused(TransferEngine.Run(wrong), fixture);
                wrong.ExpectedSerial = receipt.Serial;
                wrong.ExpectedCapacity = receipt.Capacity + 1;
                Refused(TransferEngine.Run(wrong), fixture);
            }
        }

        private static void AlteredDestination()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("Remote"))).Receipt;
                File.WriteAllBytes(receipt.DestinationPath, Changed(fixture.Data));
                Refused(TransferEngine.Run(fixture.DeleteRequest(receipt)), fixture);
                Assert(Digest(File.ReadAllBytes(fixture.Source)) == Digest(fixture.Data), "Refused delete changed source data.");
            }
        }

        private static void AlteredSource()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("Remote"))).Receipt;
                File.WriteAllBytes(fixture.Source, Changed(fixture.Data));
                // Same size AND restored timestamp must still fail the source hash check.
                File.SetLastWriteTimeUtc(fixture.Source, new DateTime(receipt.WriteTicks, DateTimeKind.Utc));
                Refused(TransferEngine.Run(fixture.DeleteRequest(receipt)), fixture);
            }
        }

        private static void FallbackReceipt()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("Local"))).Receipt;
                WorkRequest request = fixture.DeleteRequest(receipt);
                request.DestinationKind = "Remote";
                Refused(TransferEngine.Run(request), fixture);
                receipt.DestinationKind = "Fallback";
                Refused(TransferEngine.Run(request), fixture);
                receipt.DestinationKind = "network";
                Refused(TransferEngine.Run(request), fixture);
            }
        }

        private static void ReceiptMismatch()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("Remote"))).Receipt;
                WorkRequest request = fixture.DeleteRequest(receipt);
                string originalId = receipt.CardId;
                receipt.CardId = "wrong-card";
                Refused(TransferEngine.Run(request), fixture);
                receipt.CardId = originalId;
                string originalSerial = receipt.Serial;
                receipt.Serial = "wrong-volume";
                Refused(TransferEngine.Run(request), fixture);
                receipt.Serial = originalSerial;
                receipt.Capacity++;
                Refused(TransferEngine.Run(request), fixture);
                receipt.Capacity--;
                receipt.SourceRoot = fixture.DestinationRoot;
                Refused(TransferEngine.Run(request), fixture);
                receipt.SourceRoot = fixture.CardRoot;
                receipt.Sha256 = "not-a-sha256";
                Refused(TransferEngine.Run(request), fixture);
            }
        }

        private static void DeleteAfterMarkerLoss()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("Remote"))).Receipt;
                File.Delete(fixture.Marker);
                Refused(TransferEngine.Run(fixture.DeleteRequest(receipt)), fixture);
            }
        }

        private static void RemoteDelete()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("remote"))).Receipt;
                WorkRequest request = fixture.DeleteRequest(receipt);
                request.ExpectedSerial = receipt.Serial;
                request.ExpectedCapacity = receipt.Capacity;
                WorkResult result = Good(TransferEngine.Run(request));
                Assert(result.Deleted && !File.Exists(fixture.Source), "Verified source was not deleted.");
                Assert(File.Exists(fixture.Marker) && Digest(File.ReadAllBytes(receipt.DestinationPath)) == receipt.Sha256, "Delete damaged marker or verified destination.");
                WorkResult repeated = TransferEngine.Run(request);
                Assert(!repeated.Success && !repeated.Deleted, "Repeated delete should fail safely.");
            }
        }

        private static void UnsafePaths()
        {
            using (var fixture = new Fixture())
            {
                WorkRequest request = fixture.CopyRequest("Remote");
                request.File.SourcePath = Path.Combine(fixture.CardRoot, "DCIM-OTHER", "IMG.JPG");
                request.File.RelativePath = null;
                Refused(TransferEngine.Run(request), fixture);
                request = fixture.CopyRequest("Remote");
                request.File.RelativePath = "..\\escape.jpg";
                Refused(TransferEngine.Run(request), fixture);
                request = fixture.CopyRequest("Remote");
                request.File.SourcePath = Path.Combine(Path.GetDirectoryName(fixture.Source), "..", "100TEST", "IMG0001.JPG");
                Refused(TransferEngine.Run(request), fixture);
                request = fixture.CopyRequest("Remote");
                request.DestinationRoot = fixture.CardRoot;
                Refused(TransferEngine.Run(request), fixture);
                TransferReceipt receipt = Good(TransferEngine.Run(fixture.CopyRequest("Remote"))).Receipt;
                string partial = receipt.DestinationPath + ".partial";
                File.Move(receipt.DestinationPath, partial);
                receipt.DestinationPath = partial;
                Refused(TransferEngine.Run(fixture.DeleteRequest(receipt)), fixture);
                Assert(File.Exists(partial), "Refused partial receipt deleted an unowned partial.");
                request = fixture.CopyRequest("Remote");
                request.File.SourcePath += ".partial";
                Refused(TransferEngine.Run(request), fixture);
            }
        }

        private static void ReparsePaths()
        {
            using (var fixture = new Fixture())
            {
                string sourceLink = Path.Combine(fixture.CardRoot, "DCIM", "LINK");
                string targetLink = Path.Combine(fixture.Root, "DestinationLink");
                try
                {
                    CreateJunction(sourceLink, Path.GetDirectoryName(fixture.Source));
                    WorkRequest request = fixture.CopyRequest("Remote");
                    request.File.SourcePath = Path.Combine(sourceLink, Path.GetFileName(fixture.Source));
                    request.File.RelativePath = "DCIM\\LINK\\" + Path.GetFileName(fixture.Source);
                    Refused(TransferEngine.Run(request), fixture);
                    CreateJunction(targetLink, fixture.DestinationRoot);
                    request = fixture.CopyRequest("Remote");
                    request.DestinationRoot = targetLink;
                    Refused(TransferEngine.Run(request), fixture);
                    Assert(!File.Exists(fixture.Destination), "Reparse destination was followed.");
                }
                finally
                {
                    fixture.RemoveJunction(sourceLink);
                    fixture.RemoveJunction(targetLink);
                }
            }
        }

        private static void HandleLocks()
        {
            using (var fixture = new Fixture())
            {
                using (var stream = TransferNative.OpenRead(fixture.Source, false))
                {
                    ThrowsIo(delegate { using (var writer = new FileStream(fixture.Source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }, "source write");
                    ThrowsIo(delegate { File.Move(fixture.Source, fixture.Source + ".moved"); }, "source rename");
                }
                using (var directories = new TransferDirectories(Path.GetDirectoryName(fixture.Source), false))
                    ThrowsIo(delegate { Directory.Move(Path.GetDirectoryName(fixture.Source), Path.GetDirectoryName(fixture.Source) + "-moved"); }, "ancestor rename");
                using (var writer = new FileStream(fixture.Source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    Refused(TransferEngine.Run(fixture.CopyRequest("Remote")), fixture);
                Assert(File.Exists(fixture.Source), "Lock checks changed the source.");
            }
        }

        private static void RenameCollision()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                File.WriteAllBytes(fixture.Destination, other);
                string owned = Path.Combine(Path.GetDirectoryName(fixture.Destination), "owned-fixture.partial");
                using (FileStream partial = TransferNative.CreatePartial(owned))
                {
                    partial.Write(fixture.Data, 0, fixture.Data.Length);
                    partial.Flush(true);
                    Assert(!TransferNative.RenameWithoutReplace(partial.SafeFileHandle, fixture.Destination), "Atomic rename replaced an existing destination.");
                    TransferNative.MarkDelete(partial.SafeFileHandle);
                }
                Assert(!File.Exists(owned) && Digest(File.ReadAllBytes(fixture.Destination)) == Digest(other), "Owned-handle cleanup affected the destination.");
            }
        }

        private static void InvalidRequest()
        {
            Assert(!TransferEngine.Run(null).Success, "Null request escaped failure handling.");
            Assert(!TransferEngine.Run(new WorkRequest { Operation = "RemoveEverything" }).Success, "Unknown operation accepted.");
            Assert(!IdentityVerifier.Check(null, null, null, -1), "Invalid identity escaped failure handling.");
        }

        private static void UnknownFileIds()
        {
            var unknown = new TransferNative.FileInformation();
            Assert(!TransferNative.SameKnownFile(unknown, unknown), "Unavailable redirector IDs were mistaken for an alias.");
            var first = new TransferNative.FileInformation { VolumeSerial = 123, IndexLow = 42 };
            var other = new TransferNative.FileInformation { VolumeSerial = 456, IndexLow = 42 };
            Assert(!TransferNative.SameKnownFile(first, other), "File IDs from distinct volumes were treated as an alias.");
            Assert(TransferNative.SameKnownFile(first, first), "Known duplicate identity was missed.");
            first.IndexLow = 0;
            Assert(!TransferNative.SameKnownFile(first, first), "Unknown file ID on a known volume was treated as an alias.");
        }

        private static void DestinationKinds()
        {
            using (var fixture = new Fixture())
            {
                Refused(TransferEngine.Run(fixture.CopyRequest("fallback")), fixture);
                Refused(TransferEngine.Run(fixture.CopyRequest("")), fixture);
                Refused(TransferEngine.Run(fixture.CopyRequest(null)), fixture);
                Assert(Good(TransferEngine.Run(fixture.CopyRequest("LOCAL"))).Receipt.DestinationKind == "local", "Local kind was not normalized.");
            }
        }

        private static bool UnsupportedRename(SafeFileHandle handle, string path)
        {
            throw TransferNative.Failure(50, "Fixture redirector does not support handle rename.");
        }

        private static void ClassicPublication()
        {
            using (var fixture = new Fixture(196731))
            {
                bool moved = false;
                WorkResult result = Good(TransferEngine.Run(fixture.CopyRequest("remote"), UnsupportedRename,
                    delegate(string partial, string final)
                    {
                        Assert(Path.GetDirectoryName(partial) == Path.GetDirectoryName(final) && partial.EndsWith(".partial", StringComparison.Ordinal), "Classic rename escaped the owned partial directory.");
                        // The fallback may release only its destination writer, never these pins.
                        ThrowsIo(delegate { File.Move(fixture.Source, fixture.Source + ".moved"); }, "source rename during classic publication");
                        ThrowsIo(delegate { using (var writer = new FileStream(fixture.Source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }, "source write during classic publication");
                        ThrowsIo(delegate { File.Move(fixture.Marker, fixture.Marker + ".moved"); }, "identity marker rename during classic publication");
                        File.Move(partial, final);
                        moved = true;
                    }, true));
                Assert(moved && !result.ReusedExisting && !result.Deleted && File.Exists(fixture.Source), "Classic copy did not preserve its source.");
                Assert(result.Receipt.DestinationKind == "remote" && result.Receipt.DestinationPath == fixture.Destination &&
                    result.Receipt.Sha256 == Digest(fixture.Data) && Digest(File.ReadAllBytes(fixture.Destination)) == result.Receipt.Sha256, "Classic receipt was not fully verified.");
                AssertNoPartials(fixture);
                WorkResult again = Good(TransferEngine.Run(fixture.CopyRequest("remote")));
                Assert(again.ReusedExisting && again.Receipt.DestinationPath == result.Receipt.DestinationPath, "Classic final could not be safely reused.");
            }
        }

        private static void ClassicCollision()
        {
            using (var fixture = new Fixture())
            {
                int attempts = 0;
                byte[] other = Changed(fixture.Data);
                WorkResult result = Good(TransferEngine.Run(fixture.CopyRequest("remote"), UnsupportedRename,
                    delegate(string partial, string final)
                    {
                        attempts++;
                        if (attempts == 1) File.WriteAllBytes(final, other);
                        File.Move(partial, final);
                    }, true));
                Assert(attempts == 2 && result.Receipt.DestinationPath == fixture.HashDestination(1), "Classic collision did not retry a hash filename.");
                Assert(Digest(File.ReadAllBytes(fixture.Destination)) == Digest(other), "Classic rename overwrote a racing destination.");
                Assert(Digest(File.ReadAllBytes(result.Receipt.DestinationPath)) == Digest(fixture.Data) && File.Exists(fixture.Source), "Classic collision receipt/source mismatch.");
                AssertNoPartials(fixture);
            }
        }

        private static void ClassicFinalSwap()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                WorkResult result = TransferEngine.Run(fixture.CopyRequest("remote"), UnsupportedRename,
                    delegate(string partial, string final)
                    {
                        File.Move(partial, final);
                        File.WriteAllBytes(final, other); // Same-size replacement before the final opens.
                    }, true);
                Refused(result, fixture);
                Assert(result.Error.Contains("Final destination hash mismatch") && Digest(File.ReadAllBytes(fixture.Destination)) == Digest(other),
                    "Final replacement was trusted or cleaned up by pathname.");
            }
        }

        private static void ClassicPartialSwap()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                WorkResult result = TransferEngine.Run(fixture.CopyRequest("remote"), UnsupportedRename,
                    delegate(string partial, string final)
                    {
                        File.WriteAllBytes(partial, other); // The writer has closed; ownership is gone.
                        File.Move(partial, final);
                    }, true);
                Refused(result, fixture);
                Assert(result.Error.Contains("Final destination hash mismatch") && Digest(File.ReadAllBytes(fixture.Destination)) == Digest(other),
                    "Replaced partial received a receipt or was removed after ownership ended.");
            }
        }

        private static void ClassicFailureRetainsPartial()
        {
            using (var fixture = new Fixture())
            {
                string releasedPath = null;
                byte[] other = Changed(fixture.Data);
                WorkResult result = TransferEngine.Run(fixture.CopyRequest("remote"), UnsupportedRename,
                    delegate(string partial, string final)
                    {
                        releasedPath = partial;
                        File.WriteAllBytes(partial, other);
                        throw new IOException("Fixture rename failed after the writer closed.");
                    }, true);
                Refused(result, fixture);
                Assert(result.Error.Contains("NativeErrorCode=50"), "The original native rename error was lost.");
                Assert(releasedPath != null && File.Exists(releasedPath) && Digest(File.ReadAllBytes(releasedPath)) == Digest(other),
                    "Failure cleanup deleted a mutable partial pathname.");
                Assert(!File.Exists(fixture.Destination), "Failed classic publication created a receipt target.");
            }
        }

        private static void ClassicRestricted()
        {
            Assert(TransferPaths.IsWebDav(@"\\server@5005\DavWWWRoot\Photos\image.jpg"), "WebClient UNC was not recognized.");
            Assert(TransferPaths.IsWebDav(@"\\server@SSL@443\davwwwroot\Photos\image.jpg"), "TLS WebClient UNC was not recognized.");
            Assert(!TransferPaths.IsWebDav(@"\\server\Photos\DavWWWRoot\image.jpg") && !TransferPaths.IsWebDav(@"C:\DavWWWRoot\image.jpg"), "Ordinary filesystem paths enabled WebClient fallback.");
            using (var fixture = new Fixture())
            {
                bool moved = false;
                WorkResult result = TransferEngine.Run(fixture.CopyRequest("remote"), UnsupportedRename,
                    delegate(string partial, string final) { moved = true; File.Move(partial, final); }, false);
                Refused(result, fixture);
                Assert(!moved && result.Error.Contains("NativeErrorCode=50"), "Non-WebDAV native errors did not fail closed with their error code.");
                AssertNoPartials(fixture);
            }
        }

        private static void DirectReceiptCleanup()
        {
            using (var fixture = new Fixture())
            {
                TransferReceipt receipt = Good(Direct(fixture, null)).Receipt;
                WorkResult result = TransferEngine.Run(fixture.DeleteRequest(receipt));
                Assert(result.Success && result.Deleted && !File.Exists(fixture.Source), "Verified direct receipt did not clean the fixture source.");
                Assert(File.Exists(receipt.DestinationPath) && Digest(File.ReadAllBytes(receipt.DestinationPath)) == receipt.Sha256, "Cleanup altered the verified destination.");
            }
        }

        private static WorkResult Direct(Fixture fixture, Action<string, FileStream> stage)
        {
            return TransferEngine.Run(fixture.CopyRequest("remote"),
                delegate(SafeFileHandle handle, string path) { throw new InvalidOperationException("Direct mode must never rename a handle."); },
                delegate(string partial, string final) { throw new InvalidOperationException("Direct mode must never call File.Move."); },
                false, true, stage);
        }

        private static void DirectCopy()
        {
            using (var fixture = new Fixture(196731))
            {
                bool committed = false;
                WorkResult result = Good(Direct(fixture, delegate(string path, FileStream writer)
                {
                    if (writer != null)
                    {
                        Assert(writer.CanWrite && !writer.CanRead, "Direct writer must request write access only.");
                        bool deletionDenied = false;
                        try { TransferNative.MarkDelete(writer.SafeFileHandle); }
                        catch (Win32Exception error) { deletionDenied = error.NativeErrorCode == 5; }
                        Assert(deletionDenied, "Direct writer unexpectedly has DELETE access.");
                        ThrowsIo(delegate { File.Move(fixture.Source, fixture.Source + ".moved"); }, "source rename during direct copy");
                    }
                    else
                    {
                        // This open would fail if the write handle had not closed before readback.
                        committed = Digest(File.ReadAllBytes(path)) == Digest(fixture.Data);
                    }
                }));
                Assert(committed && !result.Deleted && File.Exists(fixture.Source), "Direct copy was not committed or removed the source.");
                Assert(result.Receipt.DestinationPath == fixture.Destination && result.Receipt.DestinationKind == "remote" &&
                    result.Receipt.Sha256 == Digest(fixture.Data), "Direct receipt mismatch.");
                AssertNoPartials(fixture);
            }
        }

        private static void DirectInterrupted()
        {
            using (var fixture = new Fixture())
            {
                WorkResult result = Direct(fixture, delegate(string path, FileStream writer)
                {
                    if (writer != null)
                    {
                        writer.WriteByte(123);
                        throw new IOException("Fixture interrupted direct write.");
                    }
                });
                Refused(result, fixture);
                Assert(result.Error.Contains("incomplete or unverified final file may remain") && new FileInfo(fixture.Destination).Length == 1,
                    "Interrupted direct write was cleaned up or not clearly reported.");
                WorkResult retry = Good(Direct(fixture, null));
                Assert(retry.Receipt.DestinationPath == fixture.HashDestination(1) && new FileInfo(fixture.Destination).Length == 1,
                    "Retry trusted or overwrote the incomplete final file.");
            }
        }

        private static void DirectTampered()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                WorkResult result = Direct(fixture, delegate(string path, FileStream writer)
                {
                    if (writer == null) File.WriteAllBytes(path, other);
                });
                Refused(result, fixture);
                Assert(result.Error.Contains("Final destination hash mismatch after direct copy") && Digest(File.ReadAllBytes(fixture.Destination)) == Digest(other),
                    "Same-size final tampering was trusted or removed by cleanup.");
            }
        }

        private static void DirectCollision()
        {
            using (var fixture = new Fixture())
            {
                byte[] other = Changed(fixture.Data);
                File.WriteAllBytes(fixture.Destination, other);
                bool collision = false;
                try { using (FileStream writer = TransferNative.CreateFinal(fixture.Destination)) { } }
                catch (Win32Exception error) { collision = error.NativeErrorCode == 80 || error.NativeErrorCode == 183; }
                Assert(collision, "Direct CREATE_NEW accepted an existing destination.");
                WorkResult result = Good(Direct(fixture, null));
                Assert(result.Receipt.DestinationPath == fixture.HashDestination(1) && Digest(File.ReadAllBytes(fixture.Destination)) == Digest(other),
                    "Direct collision overwrote existing content or chose the wrong receipt path.");
                WorkResult reused = Good(Direct(fixture, null));
                Assert(reused.ReusedExisting && reused.Receipt.DestinationPath == result.Receipt.DestinationPath &&
                    Digest(File.ReadAllBytes(reused.Receipt.DestinationPath)) == reused.Receipt.Sha256, "Direct retry did not fully verify the identical target.");
                AssertNoPartials(fixture);
            }
        }

        private static WorkResult Good(WorkResult result)
        {
            Assert(result != null && result.Success && result.Receipt != null, result == null ? "No result." : result.Error);
            return result;
        }

        private static void Refused(WorkResult result, Fixture fixture)
        {
            Assert(result != null && !result.Success && !result.Deleted && result.Receipt == null && !String.IsNullOrEmpty(result.Error), "Unsafe request did not fail closed.");
            Assert(File.Exists(fixture.Source), "Refused operation removed the source.");
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message ?? "Assertion failed."); }
        private static void AssertNoPartials(Fixture fixture) { Assert(Directory.GetFiles(fixture.DestinationRoot, "*.partial", SearchOption.AllDirectories).Length == 0, "Owned partial was left after success."); }
        private static byte[] Changed(byte[] value) { byte[] other = (byte[])value.Clone(); other[0] ^= 255; return other; }
        private static string Digest(byte[] value) { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        private static void ThrowsIo(Action action, string operation)
        {
            try { action(); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            throw new InvalidOperationException("Expected filesystem sharing protection for " + operation + ".");
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Root, CardRoot, DestinationRoot, CardId, Source, Destination, Marker;
            internal readonly byte[] Data;
            internal readonly MediaItem Snapshot;
            private readonly string parent;
            private readonly string uniqueName;

            internal Fixture() : this(4096) { }
            internal Fixture(int length)
            {
                parent = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PhotoImport", "v2"));
                uniqueName = "transfer-fixture-" + Guid.NewGuid().ToString("N");
                Root = Path.Combine(parent, uniqueName);
                Assert(!Directory.Exists(Root) && !File.Exists(Root), "Fixture path already exists.");
                CardRoot = Path.Combine(Root, "Card");
                DestinationRoot = Path.Combine(Root, "Destination");
                CardId = Guid.NewGuid().ToString("D");
                Source = Path.Combine(CardRoot, "DCIM", "100TEST", "IMG0001.JPG");
                Destination = Path.Combine(DestinationRoot, "DCIM", "100TEST", "IMG0001.JPG");
                Marker = Path.Combine(CardRoot, IdentityVerifier.MarkerFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(Source));
                Directory.CreateDirectory(Path.GetDirectoryName(Destination));
                WriteMarker(1, CardId);
                Data = new byte[length];
                for (int index = 0; index < Data.Length; index++) Data[index] = (byte)((index * 31 + index / 257) % 251);
                File.WriteAllBytes(Source, Data);
                File.SetLastWriteTimeUtc(Source, new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc));
                Snapshot = new MediaItem { SourcePath = Source, RelativePath = "DCIM\\100TEST\\IMG0001.JPG", Length = length, WriteTicks = File.GetLastWriteTimeUtc(Source).Ticks };
            }

            internal void WriteMarker(int version, string cardId)
            {
                JsonFile.Write(Marker, new CardMarker { Version = version, CardId = cardId, CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
            }

            internal WorkRequest CopyRequest(string kind)
            {
                return new WorkRequest
                {
                    Operation = "copy", SourceRoot = CardRoot, CardId = CardId, ExpectedSerial = "", ExpectedCapacity = 0,
                    DestinationRoot = DestinationRoot, DestinationKind = kind,
                    File = new MediaItem { SourcePath = Snapshot.SourcePath, RelativePath = Snapshot.RelativePath, Length = Snapshot.Length, WriteTicks = Snapshot.WriteTicks }
                };
            }

            internal WorkRequest DeleteRequest(TransferReceipt receipt)
            {
                return new WorkRequest
                {
                    Operation = "delete", SourceRoot = CardRoot, CardId = CardId,
                    ExpectedSerial = "", ExpectedCapacity = 0, Receipt = receipt
                };
            }

            internal string HashDestination(int attempt)
            {
                if (attempt == 0) return Destination;
                return Path.Combine(Path.GetDirectoryName(Destination), Path.GetFileNameWithoutExtension(Destination) + "." + Digest(Data) +
                    (attempt == 1 ? "" : "." + (attempt - 1).ToString(CultureInfo.InvariantCulture)) + Path.GetExtension(Destination));
            }

            internal void RemoveJunction(string path)
            {
                AssertOwned(path);
                if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path, false);
            }

            private void AssertOwned(string path)
            {
                string full = Path.GetFullPath(path);
                Assert(Path.GetDirectoryName(Root) == parent && Path.GetFileName(Root) == uniqueName &&
                    (full == Root || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Refusing cleanup outside the unique fixture root.");
            }

            // Enumerate only this fixture and delete leaves individually; never recurse into
            // a reparse point, and never issue Directory.Delete(..., true).
            private void RemoveOwnedDirectory(string directory)
            {
                AssertOwned(directory);
                Assert((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "Refusing fixture cleanup through a reparse point.");
                foreach (string path in Directory.GetFileSystemEntries(directory))
                {
                    AssertOwned(path);
                    FileAttributes attributes = File.GetAttributes(path);
                    Assert((attributes & FileAttributes.ReparsePoint) == 0, "Unexpected reparse point in fixture cleanup.");
                    if ((attributes & FileAttributes.Directory) != 0) RemoveOwnedDirectory(path);
                    else File.Delete(path);
                }
                Directory.Delete(directory, false);
            }

            public void Dispose() { if (Directory.Exists(Root)) RemoveOwnedDirectory(Root); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle OpenJunction(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

        private static void CreateJunction(string link, string target)
        {
            Directory.CreateDirectory(link);
            byte[] substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
            byte[] display = Encoding.Unicode.GetBytes(target);
            byte[] data = new byte[16 + substitute.Length + 2 + display.Length + 2];
            Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, data, 0, 4);
            Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
            Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
            Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
            Array.Copy(BitConverter.GetBytes((ushort)display.Length), 0, data, 14, 2);
            Array.Copy(substitute, 0, data, 16, substitute.Length);
            Array.Copy(display, 0, data, 18 + substitute.Length, display.Length);
            using (SafeFileHandle handle = OpenJunction(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw TransferNative.Failure(Marshal.GetLastWin32Error(), "Cannot open the owned junction fixture directory.");
                int returned;
                if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw TransferNative.Failure(Marshal.GetLastWin32Error(), "Cannot set the owned fixture junction reparse data.");
            }
        }
    }
}
