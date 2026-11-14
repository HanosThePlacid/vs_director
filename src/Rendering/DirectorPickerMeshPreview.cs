using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace VSDirector.Rendering;

/// <summary>Small transparent, depth-tested actor preview. Owns no world/entity state.</summary>
internal sealed class DirectorPickerMeshPreview : IDisposable
{
    private int program, vao, buffer, framebuffer, depth, texture;
    private const int Resolution = 256;
    private readonly List<(int Texture, float[] Vertices)> batches = new();
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
        batches.Clear(); batches.AddRange(groups.Select(g => (g.Key, g.Value.ToArray())));
        if (batches.Count == 0) throw new InvalidDataException("Actor preview has no textured triangles.");
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
                GL.BufferData(BufferTarget.ArrayBuffer, batch.Vertices.Length * sizeof(float), batch.Vertices, BufferUsageHint.StreamDraw);
                GL.DrawArrays(PrimitiveType.Triangles, 0, batch.Vertices.Length / 12);
            }
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
            uniform vec3 center; uniform float radius, yaw, aspect;
            out vec2 texcoord; out vec4 tint;
            vec3 turn(vec3 p) { float c=cos(yaw),s=sin(yaw); vec3 v=vec3(c*p.x+s*p.z,p.y,-s*p.x+c*p.z);
                return vec3(v.x,cos(-.35)*v.y-sin(-.35)*v.z,sin(-.35)*v.y+cos(-.35)*v.z); }
            void main() { vec3 p=turn(position-center)/radius; gl_Position=vec4(p.x*min(1.0,1.0/aspect),p.y*min(1.0,aspect),-p.z*.5,1);
                texcoord=uv; tint=vec4(color.rgb*(.65+.35*abs(dot(turn(normal),normalize(vec3(-.4,1,1))))),color.a); }
            """;
        const string fragment = """
            #version 330 core
            uniform sampler2D image; in vec2 texcoord; in vec4 tint; out vec4 outputColor;
            void main() { vec4 c=texture(image,texcoord)*tint; if(c.a<.05) discard; outputColor=c; }
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
            vao = GL.GenVertexArray(); buffer = GL.GenBuffer(); GL.BindVertexArray(vao); GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            int[] sizes = [3, 2, 3, 4]; int offset = 0;
            for (int i = 0; i < sizes.Length; i++) { GL.EnableVertexAttribArray(i); GL.VertexAttribPointer(i, sizes[i], VertexAttribPointerType.Float, false, 12 * sizeof(float), offset * sizeof(float)); offset += sizes[i]; }
            texture = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Resolution, Resolution, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
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
        if (texture != 0) GL.DeleteTexture(texture); if (depth != 0) GL.DeleteRenderbuffer(depth);
        if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer); if (buffer != 0) GL.DeleteBuffer(buffer);
        if (vao != 0) GL.DeleteVertexArray(vao); if (program != 0) GL.DeleteProgram(program);
        texture = depth = framebuffer = buffer = vao = program = 0;
    }
}
