using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCLLF.MMCC.Core;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PCLCS;

// Original LF host implementation; repository LICENCE applies.
public sealed class MmcInstanceExportBackend {
    /// <summary>One dedicated worker retains synchronous quiescence/admission. Callbacks must not wait for UI.</summary>
    public Task<MmcInstanceExportPreview> PreviewAsync(MmcInstanceExportRequest request,CancellationToken cancellationToken=default) =>
        Task.Run(()=>{var result=Run(request,cancellationToken,null,false);if(result.Status==MmcInstanceExportStatus.Cancelled)throw new OperationCanceledException(cancellationToken);if(result.Status!=MmcInstanceExportStatus.Completed||result.Preview is null)throw new IOException(result.Failure??"No export preview was created.");return result.Preview;},CancellationToken.None);
    /// <summary>Cancellation completes only after entry streams, ZipArchive and its file writer have stopped.</summary>
    public Task<MmcInstanceExportResult> ExportAsync(MmcInstanceExportRequest request,CancellationToken cancellationToken=default,IProgress<MmcInstanceExportProgress>? progress=null) =>
        Task.Run(()=>Run(request,cancellationToken,progress,true),CancellationToken.None);

    private static MmcInstanceExportResult Run(MmcInstanceExportRequest request,CancellationToken token,IProgress<MmcInstanceExportProgress>? progress,bool write) {
        string? partial=null;MmcInstanceExportPreview? preview=null;
        try {
            ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(request.Options);token.ThrowIfCancellationRequested();
            if(request.ExpectedSourceInstanceId==Guid.Empty||request.ExpectedSourceGeneration<1||!MmcDurableFiles.IsHash(request.ExpectedSourcePrimarySha256))throw new ArgumentException("Export requires a fixed source ID, generation and primary SHA256.");
            if(string.IsNullOrWhiteSpace(request.DisplayName)||request.DisplayName.Length>256||request.DisplayName.Any(char.IsControl))throw new ArgumentException("Export display name is invalid.");
            if(!Enum.IsDefined(request.Options.InactiveGameTree))throw new ArgumentException("Unknown inactive-game-tree export policy.");
            string root=MmcCanonicalPaths.ForAdmission(MmcInstancePaths.ValidateVersionRoot(request.SourceRoot));
            string target=MmcInstanceExportPaths.Output(request.OutputZipPath,root);
            using var quiet=MmcConfigurationCoordinator.Quiesce(root,token);
            using var lease=MmcAdmissionGate.Acquire(root,request.ExpectedSourceInstanceId,TimeSpan.Zero,token);
            var snapshot=MmcInstanceExportSnapshot.Capture(request,quiet,lease,token);preview=snapshot.Preview;
            if(request.ExpectedPreviewFingerprint is not null&&request.ExpectedPreviewFingerprint!=preview.Fingerprint)throw new IOException("Export source/options/preferences changed since preview.");
            request.Checkpoint?.Invoke(MmcInstanceExportCheckpoint.SnapshotPrepared,-1);
            if(!write)return new(MmcInstanceExportStatus.Completed,Preview:preview);
            string id=Guid.NewGuid().ToString("N");partial=Path.Combine(Path.GetDirectoryName(target)!,".pcllf-mmc-export-"+id+".partial");
            MmcDurableFiles.WriteNew(partial+".owner.json",JsonSerializer.SerializeToUtf8Bytes(new{Protocol="PCLLF.MmcExportWorkingFile/v1",OperationId=id,TargetFile=Path.GetFileName(target),PreviewFingerprint=preview.Fingerprint}));
            Write(snapshot,partial,request,token,progress);
            // Read the completed ZIP through the production bounded input reader. This is not
            // extraction and adds no download/installer path; every payload is hashed in place.
            string zipHash=VerifyArchive(snapshot,partial,token);
            request.Checkpoint?.Invoke(MmcInstanceExportCheckpoint.BeforeSourceRevalidation,-1);
            request.Checkpoint?.Invoke(MmcInstanceExportCheckpoint.BeforePublication,-1);
            var final=MmcInstanceExportSnapshot.Capture(request,quiet,lease,token);
            if(final.Preview.Fingerprint!=snapshot.Preview.Fingerprint)throw new IOException("Export source files/preferences changed before publication.");
            MmcInstanceExportSnapshot.AssertSource(request,lease);token.ThrowIfCancellationRequested();
            if(MmcInstanceExportPaths.Output(target,root)!=target)throw new IOException("Export target changed its boundary.");
            // Same-directory staging gives a same-volume, no-overwrite publication. A competing
            // creator wins safely: Move fails and its existing file is never replaced.
            File.Move(partial,target,false);
            try { progress?.Report(new("export-completed",preview.EntryCount,preview.EntryCount,preview.UncompressedBytes,preview.UncompressedBytes)); }
            catch(Exception observerError) when(observerError is OperationCanceledException or IOException or InvalidOperationException or ArgumentException) {
                // Atomic publication is the outcome boundary. A late UI/progress observer cannot
                // turn a completed file into "cancelled" while that finished ZIP exists on disk.
            }
            return new(MmcInstanceExportStatus.Completed,target,zipHash,preview);
        } catch(OperationCanceledException error) {return new(MmcInstanceExportStatus.Cancelled,Preview:preview,RetainedPartialFile:Existing(partial),Failure:error.Message);}
        catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or FormatException or System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException or AggregateException) {
            return new(partial is null?MmcInstanceExportStatus.Rejected:MmcInstanceExportStatus.Failed,Preview:preview,RetainedPartialFile:Existing(partial),Failure:error.Message);
        }
    }
    private static string? Existing(string? path)=>path is not null&&File.Exists(path)?path:null;
    private static void Write(MmcExportSnapshot snapshot,string path,MmcInstanceExportRequest request,CancellationToken token,IProgress<MmcInstanceExportProgress>? progress) {
        using FileStream output=new(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,131072,FileOptions.SequentialScan);
        long bytes=0;int count=0;
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true,new UTF8Encoding(false,true))) {
            foreach(string directory in snapshot.Directories) {token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(directory+"/");entry.LastWriteTime=new(2000,1,1,0,0,0,TimeSpan.Zero);}
            foreach(var metadata in snapshot.Metadata.Files.OrderBy(pair=>pair.Key,StringComparer.Ordinal)) {
                token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(metadata.Key,CompressionLevel.Optimal);entry.LastWriteTime=new(2000,1,1,0,0,0,TimeSpan.Zero);
                using(var target=entry.Open())target.Write(metadata.Value);
                bytes+=metadata.Value.Length;request.Checkpoint?.Invoke(MmcInstanceExportCheckpoint.EntryWritten,count++);
                progress?.Report(new("writing-portable-metadata",count,snapshot.Preview.EntryCount,bytes,snapshot.Preview.UncompressedBytes));
            }
            foreach(var file in snapshot.SourceFiles.Where(file=>file.ZipPath is not null).OrderBy(file=>file.ZipPath,StringComparer.Ordinal)) {
                token.ThrowIfCancellationRequested();string source=snapshot.Paths.ResolveRelative(MmcPathRole.Instance,file.SourceRelativePath);
                var entry=zip.CreateEntry(file.ZipPath!,CompressionLevel.Optimal);entry.LastWriteTime=new(2000,1,1,0,0,0,TimeSpan.Zero);
                using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.SequentialScan))
                using(var target=entry.Open()) {
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[131072];long copied=0;int read;
                    while((read=input.Read(buffer,0,buffer.Length))!=0){token.ThrowIfCancellationRequested();copied=checked(copied+read);if(copied>file.SizeBytes)throw new IOException("Export input grew while ZIP writer was active.");hash.AppendData(buffer,0,read);target.Write(buffer,0,read);}
                    if(copied!=file.SizeBytes||Convert.ToHexStringLower(hash.GetHashAndReset())!=file.Sha256)throw new IOException("Export input changed while ZIP writer was active.");
                }
                bytes=checked(bytes+file.SizeBytes);request.Checkpoint?.Invoke(MmcInstanceExportCheckpoint.EntryWritten,count++);
                progress?.Report(new("writing-current-environment",count,snapshot.Preview.EntryCount,bytes,snapshot.Preview.UncompressedBytes));
            }
        }
        output.Flush(true);
        if(output.Length>MmcInstanceExportSnapshot.Limits.MaxArchiveBytes)throw new IOException("Completed export ZIP exceeds the physical archive budget.");
    }
    private static string VerifyArchive(MmcExportSnapshot snapshot,string path,CancellationToken token) {
        using var source=MmcArchiveSourceReader.ReadAsync(path,cancellationToken:token).GetAwaiter().GetResult();
        if(source.Package.RelativeRoot!=""||source.Package.GameRootLeaf!=snapshot.Paths.GameRootLeaf)throw new IOException("Export ZIP changed its root/game role.");
        var resolved=MmcComponentResolver.Resolve(source.Package,[]);
        if(!resolved.IsComplete)throw new IOException("Export ZIP is not a self-contained fixed MMC component definition.");
        var compiled=MmcProfileCompiler.Compile(resolved.Profile!,snapshot.Compiled.Environment);
        if(compiled.HasErrors||compiled.Profile is null)throw new IOException("Export ZIP cannot reproduce its launch profile.");
        MmcInstanceExportMetadata.Equivalent(snapshot.Compiled,compiled.Profile,snapshot.Preferences);
        var expected=snapshot.SourceFiles.Where(file=>file.ZipPath is not null).ToDictionary(file=>file.ZipPath!,file=>(file.Sha256,file.SizeBytes),StringComparer.OrdinalIgnoreCase);
        foreach(var pair in snapshot.Metadata.Files)expected.Add(pair.Key,(MmcInstanceExportSnapshot.Hash(pair.Value),pair.Value.LongLength));
        if(source.Files.Length!=expected.Count)throw new IOException("Export ZIP payload inventory changed.");
        foreach(var file in source.Files) {
            token.ThrowIfCancellationRequested();if(!expected.TryGetValue(file.RelativePath,out var fact)||file.SizeBytes!=fact.SizeBytes)throw new IOException("Unexpected export ZIP payload.");
            using Stream input=source.OpenFile(file.RelativePath);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[131072];long bytes=0;int read;
            while((read=input.Read(buffer,0,buffer.Length))!=0){token.ThrowIfCancellationRequested();bytes=checked(bytes+read);if(bytes>fact.SizeBytes)throw new IOException("Export ZIP payload exceeds its measured size.");hash.AppendData(buffer,0,read);}
            if(bytes!=fact.SizeBytes||Convert.ToHexStringLower(hash.GetHashAndReset())!=fact.Sha256)throw new IOException("Export ZIP payload SHA256 differs from the fixed snapshot.");
        }
        return source.PackageSha256;
    }
}
