using System.Numerics;
using VSDirector.Data;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace VSDirector.Rendering;

/// <summary>Small transparent, depth-tested actor preview. Owns no world/entity state.</summary>
internal sealed class DirectorPickerMeshPreview : IDisposable
{
    private int program, vao, buffer, framebuffer, depth, texture,accumulation,resolveProgram;
    private const int Resolution = 256;
    private readonly List<(int Texture, float[] Vertices, ParticleMaterial? Material)> batches = new();
    private Vector3 center;
    private float radius;
    internal int Texture => texture;
    internal void SetMesh(MeshData mesh)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        Vector3 Position(int i) => new(mesh.xyz[i * 3], mesh.xyz[i * 3 + 1], mesh.xyz[i * 3 + 2]);
        for (int i = 0; i < mesh.VerticesCount; i++) { lo = Vector3.Min(lo, Position(i)); hi = Vector3.Max(hi, Position(i)); }
        center = (lo + hi) / 2; radius = Math.Max(.01f, (hi - lo).Length() / 2);
        var groups = new Dictionary<int, List<float>>();
        for (int i = 0; i + 2 < mesh.IndicesCount; i += 3)
        {
            int face = i / Math.Max(3, mesh.IndicesPerFace);
            if (face >= mesh.TextureIndicesCount || mesh.TextureIndices[face] >= mesh.TextureIds.Length) continue;
            int tex = mesh.TextureIds[mesh.TextureIndices[face]];
            if (!groups.TryGetValue(tex, out var data)) groups[tex] = data = new();
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            var n = Vector3.Cross(Position(b) - Position(a), Position(c) - Position(a));
            n = n.LengthSquared() > 1e-12 ? Vector3.Normalize(n) : Vector3.UnitY;
            foreach (int v in new[] { a, b, c })
            {
                var p = Position(v); data.AddRange([p.X, p.Y, p.Z, mesh.Uv[v * 2], mesh.Uv[v * 2 + 1], n.X, n.Y, n.Z]);
                for (int channel = 0; channel < 4; channel++) data.Add(mesh.Rgba is { } colors && colors.Length > v * 4 + channel ? colors[v * 4 + channel] / 255f : 1);
            }
        }
        batches.Clear(); batches.AddRange(groups.Select(g => (g.Key, g.Value.ToArray(), (ParticleMaterial?)null)));
        if (batches.Count == 0) throw new InvalidDataException("Actor preview has no textured triangles.");
    }

    internal void SetParticles(IReadOnlyList<CreationSimulatedParticle> particles,double time,Func<string,int> textureSource,IReadOnlyList<CreationParticleRibbon>? ribbons=null)
    {
        var turn=Matrix4x4.CreateRotationY((float)(time*.35))*Matrix4x4.CreateRotationX(-.35f);Matrix4x4.Invert(turn,out var facing);
        var ordered=particles.OrderBy(p=>Vector3.Transform(p.Position.Float(),turn).Z);
        var built=DirectorParticleMesh.Build(ordered,facing,_=>Vector3.One);
        foreach(var r in ribbons??[])built.Add(new(new(r.Texture,r.Additive,false,false,r.Glow,new(0,0,1,1)),DirectorParticleMesh.Ribbon(r,Vector3.TransformNormal(Vector3.UnitZ,facing),_=>Vector3.One)));
        var all=new List<(int Texture,float[] Vertices,ParticleMaterial? Material)>();
        var lo=new Vector3(float.MaxValue);var hi=new Vector3(float.MinValue);
        foreach(var batch in built)
        {
            var mesh=batch.Mesh;int textureId=batch.Material.Texture.Length==0?0:textureSource(batch.Material.Texture);
            mesh.TextureIds=[textureId];mesh.TextureIndicesCount=(mesh.IndicesCount+5)/6;mesh.TextureIndices=new byte[mesh.TextureIndicesCount];
            SetMesh(mesh);foreach(var item in batches)all.Add((item.Texture,item.Vertices,batch.Material));
            for(int i=0;i<mesh.VerticesCount;i++){var p=new Vector3(mesh.xyz[i*3],mesh.xyz[i*3+1],mesh.xyz[i*3+2]);lo=Vector3.Min(lo,p);hi=Vector3.Max(hi,p);}
        }
        batches.Clear();batches.AddRange(all);
        if(all.Count>0){center=(lo+hi)/2;radius=Math.Max(.3f,(hi-lo).Length()/2);}
    }
    internal void Render(double time, float aspect)
    {
        using var state = new DirectorCreationRenderState();
        using var targetState = new DirectorCreationSelectionState();
        int renderbuffer = GL.GetInteger(GetPName.RenderbufferBinding);
        GL.ActiveTexture(TextureUnit.Texture0);
        int sampler0 = GL.GetInteger(GetPName.SamplerBinding), depthFunc = GL.GetInteger(GetPName.DepthFunc);
        double clearDepth = GL.GetDouble(GetPName.DepthClearValue);
        int unpack = GL.GetInteger(GetPName.PixelUnpackBufferBinding);
        try
        {
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            EnsureResources();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,accumulation,0);
            targetState.Begin(new FrameBufferRef { FboId = framebuffer, Width = Resolution, Height = Resolution }, false);
            GL.Enable(EnableCap.DepthTest); GL.DepthFunc(DepthFunction.Less); GL.DepthMask(true);
            GL.ClearDepth(1); GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.BindSampler(0, 0);
            GL.UseProgram(program);
            GL.Uniform1(GL.GetUniformLocation(program, "image"), 0);
            GL.Uniform1(GL.GetUniformLocation(program, "yaw"), (float)(time * .35));
            GL.Uniform1(GL.GetUniformLocation(program, "aspect"), Math.Max(.1f, aspect));
            GL.Uniform1(GL.GetUniformLocation(program, "radius"), radius / .9f);
            GL.Uniform3(GL.GetUniformLocation(program, "center"), center.X, center.Y, center.Z);
            GL.BindVertexArray(vao); GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            foreach (var batch in batches)
            {
                GL.BindTexture(TextureTarget.Texture2D, batch.Texture);
                bool particle=batch.Material.HasValue;var material=batch.Material.GetValueOrDefault();
                GL.Uniform1(GL.GetUniformLocation(program,"textured"),batch.Texture==0?0:1);
                GL.Uniform1(GL.GetUniformLocation(program,"particle"),particle?1:0);
                GL.Uniform1(GL.GetUniformLocation(program,"soft"),particle&&material.Soft&&!material.Cube?1:0);
                GL.Uniform1(GL.GetUniformLocation(program,"cube"),!particle||material.Cube?1:0);
                GL.Uniform1(GL.GetUniformLocation(program,"glow"),(float)material.Glow);
                GL.Uniform4(GL.GetUniformLocation(program,"uvRect"),material.Uv.X,material.Uv.Y,material.Uv.Z,material.Uv.W);
                if(particle){GL.Enable(EnableCap.Blend);GL.BlendEquation(BlendEquationMode.FuncAdd);GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha,material.Additive?BlendingFactorDest.One:BlendingFactorDest.OneMinusSrcAlpha,BlendingFactorSrc.One,BlendingFactorDest.OneMinusSrcAlpha);GL.DepthMask(false);}
                else{GL.Enable(EnableCap.Blend);GL.BlendEquation(BlendEquationMode.FuncAdd);GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha,BlendingFactorDest.OneMinusSrcAlpha,BlendingFactorSrc.One,BlendingFactorDest.OneMinusSrcAlpha);GL.DepthMask(true); }
                GL.BufferData(BufferTarget.ArrayBuffer, batch.Vertices.Length * sizeof(float), batch.Vertices, BufferUsageHint.StreamDraw);
                GL.DrawArrays(PrimitiveType.Triangles, 0, batch.Vertices.Length / 12);
            }
            // ImGui expects straight alpha. The transparent offscreen blend above produces
            // premultiplied RGB, so resolve it once instead of multiplying alpha twice.
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);
            GL.Disable(EnableCap.Blend);GL.Disable(EnableCap.DepthTest);GL.DepthMask(false);
            GL.UseProgram(resolveProgram);GL.BindTexture(TextureTarget.Texture2D,accumulation);
            GL.Uniform1(GL.GetUniformLocation(resolveProgram,"source"),0);GL.DrawArrays(PrimitiveType.Triangles,0,3);
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, unpack);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
            GL.BindSampler(0, sampler0);
            GL.DepthFunc((DepthFunction)depthFunc); GL.ClearDepth(clearDepth);
            // The render-state guard restores the original active unit and its texture; its sampler was never changed.
        }
    }
    private void EnsureResources()
    {
        if (program != 0) return;
        const string vertex = """
            #version 330 core
            layout(location=0) in vec3 position; layout(location=1) in vec2 uv;
            layout(location=2) in vec3 normal; layout(location=3) in vec4 color;
            uniform vec3 center; uniform float radius, yaw, aspect; uniform int cube; uniform float glow;
            out vec2 texcoord; out vec4 tint;
            vec3 turn(vec3 p) { float c=cos(yaw),s=sin(yaw); vec3 v=vec3(c*p.x+s*p.z,p.y,-s*p.x+c*p.z);
                return vec3(v.x,cos(-.35)*v.y-sin(-.35)*v.z,sin(-.35)*v.y+cos(-.35)*v.z); }
            void main() { vec3 p=turn(position-center)/radius; gl_Position=vec4(p.x*min(1.0,1.0/aspect),p.y*min(1.0,aspect),-p.z*.5,1);
                texcoord=uv; tint=vec4(color.rgb*mix(cube!=0?(.65+.35*abs(dot(turn(normal),normalize(vec3(-.4,1,1))))):1.0,1.0,clamp(glow,0.0,1.0)),color.a); }
            """;
        const string fragment = """
            #version 330 core
            uniform sampler2D image; uniform int textured,particle,soft; uniform vec4 uvRect; in vec2 texcoord; in vec4 tint; out vec4 outputColor;
            void main() { vec4 c=(textured!=0?texture(image,texcoord):vec4(1))*tint; if(soft!=0){vec2 uv=(texcoord-uvRect.xy)/max(uvRect.zw-uvRect.xy,vec2(.00001));c.a*=1.0-smoothstep(0.0,1.0,length(uv*2.0-1.0));} if(c.a<(particle!=0?.01:.05)) discard; outputColor=c; }
            """;
        int Compile(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) { string message = GL.GetShaderInfoLog(shader); GL.DeleteShader(shader); throw new InvalidOperationException(message); }
            return shader;
        }
        int vs = 0, fs = 0;
        try
        {
            vs = Compile(ShaderType.VertexShader, vertex); fs = Compile(ShaderType.FragmentShader, fragment);
            program = GL.CreateProgram(); GL.AttachShader(program, vs); GL.AttachShader(program, fs); GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
            int resolveVertex=Compile(ShaderType.VertexShader,"#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}");
            int resolveFragment=0;
            try
            {
                resolveFragment=Compile(ShaderType.FragmentShader,"#version 330 core\nuniform sampler2D source;out vec4 outputColor;void main(){vec4 c=texelFetch(source,ivec2(gl_FragCoord.xy),0);outputColor=vec4(c.a>0.00001?c.rgb/c.a:vec3(0),c.a);}");
                resolveProgram=GL.CreateProgram();GL.AttachShader(resolveProgram,resolveVertex);GL.AttachShader(resolveProgram,resolveFragment);GL.LinkProgram(resolveProgram);
                GL.GetProgram(resolveProgram,GetProgramParameterName.LinkStatus,out int resolved);if(resolved==0)throw new InvalidOperationException(GL.GetProgramInfoLog(resolveProgram));
            }
            finally{GL.DeleteShader(resolveVertex);if(resolveFragment!=0)GL.DeleteShader(resolveFragment);}
            vao = GL.GenVertexArray(); buffer = GL.GenBuffer(); GL.BindVertexArray(vao); GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            int[] sizes = [3, 2, 3, 4]; int offset = 0;
            for (int i = 0; i < sizes.Length; i++) { GL.EnableVertexAttribArray(i); GL.VertexAttribPointer(i, sizes[i], VertexAttribPointerType.Float, false, 12 * sizeof(float), offset * sizeof(float)); offset += sizes[i]; }
            texture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Resolution, Resolution, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            accumulation=GL.GenTexture();GL.BindTexture(TextureTarget.Texture2D,accumulation);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,Resolution,Resolution,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            framebuffer = GL.GenFramebuffer(); GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
            depth = GL.GenRenderbuffer(); GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, Resolution, Resolution);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new InvalidOperationException("Actor preview framebuffer is incomplete.");
        }
        catch { Dispose(); throw; }
        finally { if (vs != 0) GL.DeleteShader(vs); if (fs != 0) GL.DeleteShader(fs); }
    }
    public void Dispose()
    {
        if(accumulation!=0)GL.DeleteTexture(accumulation);if(resolveProgram!=0)GL.DeleteProgram(resolveProgram);
        if (texture != 0) GL.DeleteTexture(texture); if (depth != 0) GL.DeleteRenderbuffer(depth);
        if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer); if (buffer != 0) GL.DeleteBuffer(buffer);
        if (vao != 0) GL.DeleteVertexArray(vao); if (program != 0) GL.DeleteProgram(program);
        accumulation=resolveProgram=texture = depth = framebuffer = buffer = vao = program = 0;
    }
}
