using System.Net;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Lumibelle.Testing;
namespace Lumibelle.Tests;
public sealed partial class ShotTests
{
    [Fact]
    public async Task InterruptedArchiveDownloadResumesExactFramesWithoutRepeatingCompletedTransfers()
    {
        var f=Fixture();var shot=Ready();var snap=Snapshot(f.Project.Id,shot);var run=new VideoRun{Snapshot=snap};var c=new VideoCandidate{Number=1};
        c.Output=JsonSerializer.SerializeToElement(new{outputs=new Dictionary<string,object>{["14"]=new{images=new[]{new{filename="video.mp4"}}},["15"]=new{images=new[]{new{filename="frames-0.webp"},new{filename="frames-1.webp"}}}}});
        var first=await MockFrameArchive.WebpAsync(32,32,24,ct:_ct);var second=await MockFrameArchive.WebpAsync(32,32,snap.FrameCount-24,24,ct:_ct);
        bool fail=true;Dictionary<string,int> requests=[];
        using var handler=new ScriptedHttpHandler((r,ct)=>{
            var file=System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)["filename"]!;requests[file]=requests.GetValueOrDefault(file)+1;
            if(file=="frames-1.webp"&&fail)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(file.EndsWith("mp4")?[1,2,3]:file=="frames-0.webp"?first:second)});
        });
        var generator=new ComfyH3Video(new TestHttpFactory(handler),new BenchmarkComfyMonitor(),f.Assets,f.Assets,f.Shots,new MockMediaTools(snap.FrameCount));
        var dir=Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id,run.Id,_ct),"candidate-1");
        await Assert.ThrowsAsync<HttpRequestException>(()=>generator.DownloadAsync(run,c,dir,_=>Task.CompletedTask,_ct));
        Assert.True(File.Exists(Path.Combine(dir,"archive-0000.webp")));Assert.False(File.Exists(Path.Combine(dir,"archive-0001.webp")));Assert.Empty((await f.Shots.LoadAsync(f.Project.Id,_ct)).Takes);
        fail=false;var take=await generator.DownloadAsync(run,c,dir,_=>Task.CompletedTask,_ct);
        Assert.Equal(1,requests["video.mp4"]);Assert.Equal(1,requests["frames-0.webp"]);Assert.Equal(2,requests["frames-1.webp"]);Assert.Equal(snap.FrameCount,take.Frames.Count);
        Assert.Equal(Enumerable.Range(0,snap.FrameCount),take.Frames.Select(f=>f.Index));Assert.All(take.Frames,frame=>Assert.True(frame.Bytes>0));
        Assert.Equal(3+take.Frames.DistinctBy(f=>f.FileName).Sum(f=>f.Bytes),take.Bytes);Assert.Empty(Directory.GetFiles(dir,"*.png"));
    }
    [Fact]
    public async Task ImageInputsUseIndependentCropsAndSnapshotOrder()
    {
        var f=Fixture();var asset=new ReferenceAsset{Id=Guid.NewGuid(),Name="Mira"};var library=await f.Assets.SaveAsync(new(){ProjectId=f.Project.Id,Assets=[asset]},0,_ct);
        for(var i=0;i<2;i++){using var png=new MemoryStream(AssetStoreTests.Png(80,40));library=await f.Assets.AddImageAsync(f.Project.Id,asset.Id,png,new("source.png",[],AssetImageOrigin.Imported),library.Revision,_ct);}
        var shot=Ready();shot.Images=library.Assets[0].Images.Select((image,i)=>new ShotImageBinding{AssetId=asset.Id,MediaId=image.Id,Name=$"Image {i}",Crop=i==0?new(){X=0,Y=0,Width=.5,Height=1}:new(){X=0,Y=0,Width=1,Height=.5}}).ToList();
        var run=new VideoRun{Snapshot=Snapshot(f.Project.Id,shot)};var dir=await f.Shots.RunDirectoryAsync(f.Project.Id,run.Id,_ct);
        using var handler=new ScriptedHttpHandler((r,ct)=>throw new Exception("Preparation must not contact ComfyUI"));
        var generator=new ComfyH3Video(new TestHttpFactory(handler),new BenchmarkComfyMonitor(),f.Assets,f.Assets,f.Shots,new MockMediaTools(39));
        await generator.PrepareAsync(run,dir,_ct);Assert.True(run.InputsPrepared);Assert.Equal(2,run.Inputs.Count);
        using var first=await Image.LoadAsync<Rgb24>(Path.Combine(dir,"inputs",run.Inputs[0].FileName),_ct);using var second=await Image.LoadAsync<Rgb24>(Path.Combine(dir,"inputs",run.Inputs[1].FileName),_ct);
        Assert.Equal((40,40),(first.Width,first.Height));Assert.Equal((80,20),(second.Width,second.Height));Assert.All(run.Inputs,i=>Assert.False(i.Audio));
        var deletion=await f.Assets.DeleteImageAsync(f.Project.Id,asset.Id,shot.Images[0].MediaId,library.Revision,_ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(()=>generator.ValidateInputsAsync(run.Snapshot,_ct));
        Assert.True(File.Exists(Path.Combine(dir,"inputs",run.Inputs[0].FileName)));
    }
    [Fact]
    public async Task CancelNeverUsesTheGlobalInterruptEndpoint()
    {
        var f=Fixture();using var handler=new ScriptedHttpHandler((r,ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var generator=new ComfyH3Video(new TestHttpFactory(handler),new BenchmarkComfyMonitor(),f.Assets,f.Assets,f.Shots,new MockMediaTools(39));
        var run=new VideoRun{Snapshot=Snapshot(f.Project.Id,Ready())};var c=new VideoCandidate{Number=1,PromptId=Guid.NewGuid().ToString()};
        Assert.False(await generator.CancelAsync(run,c,_ct));Assert.DoesNotContain(handler.Requests,r=>r.Path.Contains("interrupt"));Assert.Contains(handler.Requests,r=>r.Path.Contains(c.PromptId));
    }
    [Theory]
    [InlineData("", "Could not check H3 against http://video.test:8188/: the request failed. Check this ComfyUI connection, then retry.")]
    [InlineData("The server is offline", "Could not check H3 against http://video.test:8188/: The server is offline. Check this ComfyUI connection, then retry.")]
    public async Task H3CheckReportsTheConnectionReasonInsteadOfTheGenericRefreshMessage(string reason, string expected)
    {
        var f=Fixture();using var handler=new ScriptedHttpHandler((r,ct)=>throw new HttpRequestException(reason));
        var generator=new ComfyH3Video(new TestHttpFactory(handler),new BenchmarkComfyMonitor(),f.Assets,f.Assets,f.Shots,new MockMediaTools(39));
        var check=await generator.CheckAsync(new() { ComfyUrl="http://video.test:8188", H3=new() }, _ct);
        Assert.Equal(expected, check.Message);
    }
    [Fact]
    public void DuplicateAndOutOfRangeReferencesAreRejected()
    {
        var shot=Ready();var binding=new ShotImageBinding{AssetId=Guid.NewGuid(),MediaId=Guid.NewGuid(),Name="Mira"};shot.Images=[binding,ShotCopy.Of(binding)];
        Assert.Throws<WorkspaceStoreException>(()=>H3Policy.Validate(shot,true));
        shot.Images=[binding];binding.Crop=new(){Width=1.1,Height=1};Assert.Throws<WorkspaceStoreException>(()=>H3Policy.Validate(shot,true));
        shot.Images=Enumerable.Range(0,10).Select(i=>new ShotImageBinding{AssetId=Guid.NewGuid(),MediaId=Guid.NewGuid(),Name="Image"}).ToList();Assert.Throws<WorkspaceStoreException>(()=>H3Policy.Validate(shot,true));
    }
    private sealed class MockMediaTools(int frames):IProductionMediaTools
    {
        public Task<double> AudioDurationAsync(string p,H3Settings s,CancellationToken ct)=>throw new NotSupportedException();
        public Task PrepareVoiceAsync(string a,string b,double start,double duration,H3Settings s,CancellationToken ct)=>throw new NotSupportedException();
        public Task<VideoFileInfo> VideoInfoAsync(string p,H3Settings s,CancellationToken ct)=>Task.FromResult(new VideoFileInfo(32,32,frames,24,true));
    }
}
