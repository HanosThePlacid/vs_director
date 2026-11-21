using System.Diagnostics;
using System.Globalization;
using VSDirector.Data;
using VSDirector.Recording;

namespace VSDirector.Blueprints;

/// <summary>Bounded frame capture, asynchronous disk/encoder work, and atomic publication.</summary>
internal sealed class DirectorProjectionExport : IDisposable
{
    private readonly DirectorProjectionScene scene;
    private readonly bool apng;
    private bool disposed;
    private readonly string executable,output,temporary,raw;
    private FileStream? stream;
    private Task? write,encode;
    private readonly CancellationTokenSource cancellation=new();
    private int frame;
    internal bool Done { get; private set; }
    internal string Error { get; private set; }="";
    internal double Progress=>Done?1:encode is not null?.95:.9*frame/scene.View.Frames;
    internal DirectorProjectionExport(DirectorProjectionScene scene,string output,string ffmpeg)
    {
        this.scene=scene;scene.View.Validate();apng=scene.View.Apng;
        if(scene.Build.Issues.Count>0||scene.Build.Error.Length>0)throw new InvalidOperationException(string.Join("\n",scene.Build.Issues.Append(scene.Build.Error)));
        executable=DirectorFfmpegLocator.ResolveRequired(ffmpeg);this.output=Path.GetFullPath(Path.ChangeExtension(output,apng?".apng":".gif"));
        if(File.Exists(this.output))throw new IOException("Output already exists. Choose another file name.");
        Directory.CreateDirectory(Path.GetDirectoryName(this.output)!);
        temporary=this.output+"."+Guid.NewGuid().ToString("N")+".partial";
        raw=Path.Combine(Path.GetTempPath(),"vsdirector-projection-"+Guid.NewGuid().ToString("N")+".rgba");
        long required=(long)scene.View.Width*scene.View.Height*4*scene.View.Frames;
        if(required>8L*1024*1024*1024)throw new InvalidOperationException("Animation requires more than 8 GiB of temporary frames. Reduce resolution or duration.");
        var drive=new DriveInfo(Path.GetPathRoot(raw)!);
        if(drive.AvailableFreeSpace<required+64L*1024*1024)throw new IOException("Insufficient temporary disk space for animation frames.");
        stream=new(raw,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous);
    }
    internal void Tick()
    {
        if(Done)return;
        try
        {
            if(encode is not null){if(!encode.IsCompleted)return;encode.GetAwaiter().GetResult();Done=true;return;}
            if(write is not null){if(!write.IsCompleted)return;write.GetAwaiter().GetResult();write=null;}
            if(frame<scene.View.Frames)
            {
                if(!scene.Render(frame/(double)scene.View.Fps,scene.View.Width,scene.View.Height))return;
                var bytes=scene.Renderer.ReadRgba();write=stream!.WriteAsync(bytes,cancellation.Token).AsTask();frame++;return;
            }
            stream!.Dispose();stream=null;encode=EncodeAsync(executable,raw,temporary,output,scene.View,cancellation.Token);
        }
        catch(Exception e){Error=e.GetBaseException().Message;Done=true;Cleanup();}
    }
    internal static async Task EncodeAsync(string executable,string raw,string temporary,string output,CreationProjectionView view,CancellationToken token)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        string[] common=["-hide_banner","-loglevel","error","-y","-f","rawvideo","-pixel_format","rgba","-video_size",$"{view.Width}x{view.Height}","-framerate",view.Fps.ToString(CultureInfo.InvariantCulture),"-i",raw];
        foreach(var arg in common)info.ArgumentList.Add(arg);
        string[] encoding=view.Apng?["-vf","vflip","-plays","0","-f","apng"]:
            ["-filter_complex","[0:v]vflip,split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse=dither=sierra2_4a:alpha_threshold=128","-loop","0","-f","gif"];
        foreach(var arg in encoding)info.ArgumentList.Add(arg);info.ArgumentList.Add(temporary);
        try
        {
            using var process=Process.Start(info)??throw new IOException("Could not start FFmpeg.");
            var errors=process.StandardError.ReadToEndAsync(token);
            using var registration=token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
            await process.WaitForExitAsync(token);string error=await errors;
            if(process.ExitCode!=0)throw new IOException("FFmpeg: "+error);
            token.ThrowIfCancellationRequested();File.Move(temporary,output,false);
        }
        finally{if(File.Exists(raw))File.Delete(raw);if(File.Exists(temporary))File.Delete(temporary);}
    }
    private void Cleanup()
    {
        stream?.Dispose();stream=null;
        if(File.Exists(raw))File.Delete(raw);if(File.Exists(temporary))File.Delete(temporary);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        cancellation.Cancel();
        try{write?.GetAwaiter().GetResult();encode?.GetAwaiter().GetResult();}catch(OperationCanceledException){}catch(IOException){}
        Cleanup();scene.Dispose();cancellation.Dispose();Done=true;
    }
}
