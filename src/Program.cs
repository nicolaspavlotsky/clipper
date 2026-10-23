using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Clipper;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}

record VideoInfo(int Width, int Height, double Fps, double Duration, long Size, long Bitrate, long VideoBitrate, int Audio, string VideoCodec, double StartTime);

record ExportRequest(double Start, double End, string Mode, int VideoKbps, int AudioKbps, int Height);

/// <summary>Read-only view of the next <c>length</c> bytes of a stream.</summary>
sealed class SliceStream(Stream inner, long length) : Stream
{
    long pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => pos; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (pos >= length) return 0;
        int n = inner.Read(buffer, offset, (int)Math.Min(count, length - pos));
        pos += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

sealed class MainForm : Form
{
    const string AppHost = "app.clipper", MediaHost = "media.clipper";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipper");

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 15, 17) };
    string ffmpeg, ffprobe;
    readonly Task toolsReady;
    readonly Task<string> hwEncoder;   // h264_nvenc / h264_amf / h264_qsv, or null for CPU (libx264)
    string pendingFile, curPath, lastOut;
    VideoInfo curInfo;
    Process job, storyboard;
    bool cancelled;
    int loadId;
    readonly List<byte[]> keyframes = [];   // JPEGs of the current video's keyframes, guarded by lock

    public MainForm(string file)
    {
        pendingFile = file;
        Text = "Clipper";
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 15, 17);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Controls.Add(web);
        toolsReady = Task.Run(() =>
        {
            var bundled = ExtractBundledFfmpeg();
            ffmpeg = FindTool("ffmpeg.exe", bundled);
            ffprobe = FindTool("ffprobe.exe", bundled);
        });
        hwEncoder = toolsReady.ContinueWith(_ => DetectHwEncoder(), TaskScheduler.Default);
        Load += async (_, _) => await InitWeb();
        FormClosing += (_, _) => { KillJob(); try { storyboard?.Kill(); } catch { } };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static string FindTool(string name, string bundledDir)
    {
        var dirs = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "ffmpeg"), bundledDir }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try { var p = Path.Combine(dir.Trim(), name); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Release builds embed FFmpeg (ffmpeg.zip) inside Clipper.exe. It's unpacked once to %LOCALAPPDATA%\Clipper
    /// and reused on later runs. Returns that folder, or null if this build doesn't carry FFmpeg.
    /// </summary>
    static string ExtractBundledFfmpeg()
    {
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("ffmpeg.zip");
        if (res == null) return null;
        var dir = Path.Combine(DataDir, $"ffmpeg-{res.Length}");
        var ok = Path.Combine(dir, ".ok");
        if (File.Exists(ok)) return dir;
        var tmp = $"{dir}.tmp{Environment.ProcessId}";
        try
        {
            using (var zip = new ZipArchive(res)) zip.ExtractToDirectory(tmp, true);
            File.WriteAllText(Path.Combine(tmp, ".ok"), "");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
        }
        catch
        {
            // another instance may have finished the same extraction first
            try { Directory.Delete(tmp, true); } catch { }
        }
        return File.Exists(ok) ? dir : null;
    }

    string DetectHwEncoder()
    {
        if (ffmpeg == null) return null;
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
            if (Run(ffmpeg, "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.1", "-vf", "format=nv12",
                "-frames:v", "1", "-c:v", enc, "-f", "null", "-").Code == 0)
                return enc;
        return null;
    }

    async Task InitWeb()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, DataDir);
        await web.EnsureCoreWebView2Async(env);
        var cw = web.CoreWebView2;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.WebMessageReceived += OnMessage;
        // Both the UI and the video are served from memory/handlers: no temp files, no folder mappings
        byte[] page;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
        using (var ms = new MemoryStream()) { s.CopyTo(ms); page = ms.ToArray(); }
        cw.AddWebResourceRequestedFilter($"https://{AppHost}/*", CoreWebView2WebResourceContext.All);
        cw.AddWebResourceRequestedFilter($"https://{MediaHost}/*", CoreWebView2WebResourceContext.All);
        cw.WebResourceRequested += (_, e) => e.Response = new Uri(e.Request.Uri).Host == AppHost
            ? env.CreateWebResourceResponse(new MemoryStream(page), 200, "OK", "Content-Type: text/html; charset=utf-8\nCache-Control: no-store")
            : ServeMedia(env, e.Request);
        cw.DOMContentLoaded += async (_, _) =>
        {
            await toolsReady;
            if (ffmpeg == null || ffprobe == null)
                Send(new { type = "error", msg = "ffmpeg.exe / ffprobe.exe not found. Put them next to Clipper.exe." });
            if (pendingFile != null) { var f = pendingFile; pendingFile = null; LoadVideo(f); }
        };
        cw.Navigate($"https://{AppHost}/index.html");
    }

    void Send(object msg)
    {
        if (web.CoreWebView2 != null && !IsDisposed) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg, Json));
    }

    void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("cmd").GetString())
        {
            case "open":
                BeginInvoke(OpenDialog);
                break;
            case "drop":
                var file = e.AdditionalObjects?.OfType<CoreWebView2File>().FirstOrDefault();
                if (file != null) LoadVideo(file.Path);
                break;
            case "export":
                _ = Export(new ExportRequest(m.GetProperty("start").GetDouble(), m.GetProperty("end").GetDouble(), m.GetProperty("mode").GetString(),
                    m.GetProperty("vk").GetInt32(), m.GetProperty("ak").GetInt32(), m.GetProperty("height").GetInt32()));
                break;
            case "cancel":
                cancelled = true;
                KillJob();
                break;
            case "copy":
                if (File.Exists(lastOut)) Clipboard.SetFileDropList(new StringCollection { lastOut });
                break;
            case "reveal":
                if (File.Exists(lastOut)) Process.Start("explorer.exe", $"/select,\"{lastOut}\"");
                break;
        }
    }

    void OpenDialog()
    {
        using var d = new OpenFileDialog { Title = "Open video", Filter = "Videos|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.ts|All files|*.*" };
        if (curPath != null) d.InitialDirectory = Path.GetDirectoryName(curPath);
        if (d.ShowDialog(this) == DialogResult.OK) LoadVideo(d.FileName);
    }

    async void LoadVideo(string path)
    {
        if (!File.Exists(path)) return;
        await toolsReady;
        if (ffprobe == null) return;
        var info = await Task.Run(() => Probe(path));
        if (info == null) { Send(new { type = "error", msg = "Couldn't read " + Path.GetFileName(path) }); return; }
        curPath = path;
        curInfo = info;
        var id = ++loadId;
        var name = Path.GetFileName(path);
        Text = "Clipper — " + name;
        Send(new { type = "loaded", id, url = $"https://{MediaHost}/{Uri.EscapeDataString(name)}?v={DateTime.Now.Ticks}", name, path, info });
        StartStoryboard(path, id, info.StartTime);
    }

    // Decodes only the keyframes (GPU, ~2s for a 5 min recording) into small JPEGs and streams them to the page,
    // which shows the nearest one instantly while scrubbing and uses them for the timeline strip.
    void StartStoryboard(string path, int id, double startTime)
    {
        try { storyboard?.Kill(); } catch { }
        lock (keyframes) keyframes.Clear();
        Task.Run(() =>
        {
            // CUDA decoding only exists on NVIDIA; everyone else goes straight to the CPU path
            if (!(hwEncoder.Result == "h264_nvenc" && RunStoryboard(path, id, startTime, gpu: true)) && id == loadId)
                RunStoryboard(path, id, startTime, gpu: false);
        });
    }

    bool RunStoryboard(string path, int id, double startTime, bool gpu)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var args = new List<string> { "-hide_banner", "-nostats" };
        if (gpu) args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
        args.AddRange(["-skip_frame", "nokey", "-i", path, "-map", "0:v:0",
            "-vf", gpu ? "scale_cuda=640:-2,hwdownload,format=nv12,showinfo" : "scale=640:-2:flags=fast_bilinear,showinfo",
            "-fps_mode", "passthrough", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2pipe", "-"]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        storyboard = p;
        var times = new List<double>();
        var images = new List<byte[]>();
        int sent = 0;
        void Flush()
        {
            // showinfo (stderr) and the JPEG stream (stdout) arrive independently; pair them up by index
            var batch = new List<object>();
            lock (images)
                while (sent < images.Count && sent < times.Count)
                {
                    int index;
                    lock (keyframes) { if (id != loadId) return; keyframes.Add(images[sent]); index = keyframes.Count - 1; }
                    batch.Add(new { i = index, t = times[sent] });
                    sent++;
                }
            if (batch.Count > 0) BeginInvoke(() => { if (id == loadId) Send(new { type = "keyframes", id, items = batch }); });
        }

        var errTask = Task.Run(() =>
        {
            string line;
            while ((line = p.StandardError.ReadLine()) != null)
            {
                int k = line.IndexOf("pts_time:", StringComparison.Ordinal);
                if (k < 0) continue;
                var num = line[(k + 9)..].Split(' ', 2)[0];
                if (double.TryParse(num, NumberStyles.Float, Inv, out var t))
                {
                    lock (images) times.Add(Math.Max(0, t - startTime));
                    Flush();
                }
            }
        });

        // mjpeg byte-stuffs 0xFF inside entropy data, so FF D9 only ever appears as an image's end marker
        var stdout = p.StandardOutput.BaseStream;
        var buf = new MemoryStream();
        var chunk = new byte[1 << 16];
        int n;
        byte prev = 0;
        while ((n = stdout.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (id != loadId) { try { p.Kill(); } catch { } break; }
            for (int k = 0; k < n; k++)
            {
                byte b = chunk[k];
                buf.WriteByte(b);
                if (prev == 0xFF && b == 0xD9)
                {
                    lock (images) images.Add(buf.ToArray());
                    buf.SetLength(0);
                    Flush();
                    b = 0;
                }
                prev = b;
            }
        }
        p.WaitForExit();
        errTask.Wait();
        Flush();
        return sent > 0;
    }

    // Serves the current video with HTTP range support so the player can seek anywhere instantly
    CoreWebView2WebResourceResponse ServeMedia(CoreWebView2Environment env, CoreWebView2WebResourceRequest req)
    {
        // /kf/{loadId}/{index}.jpg -> storyboard keyframe
        var segs = new Uri(req.Uri).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 3 && segs[0] == "kf")
        {
            byte[] jpg = null;
            if (int.TryParse(segs[1], out var id) && int.TryParse(Path.GetFileNameWithoutExtension(segs[2]), out var i))
                lock (keyframes) if (id == loadId && i >= 0 && i < keyframes.Count) jpg = keyframes[i];
            return jpg == null
                ? env.CreateWebResourceResponse(null, 404, "Not Found", "")
                : env.CreateWebResourceResponse(new MemoryStream(jpg), 200, "OK", "Content-Type: image/jpeg\nAccess-Control-Allow-Origin: *\nCache-Control: max-age=86400");
        }

        var path = curPath;
        if (path == null || !File.Exists(path)) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
        long len = new FileInfo(path).Length, start = 0, end = len - 1;
        var range = req.Headers.Contains("Range") ? req.Headers.GetHeader("Range") : null;
        bool partial = range != null && range.StartsWith("bytes=");
        if (partial)
        {
            var parts = range[6..].Split('-');
            if (long.TryParse(parts[0], out var a))
            {
                start = a;
                if (parts.Length > 1 && long.TryParse(parts[1], out var b)) end = Math.Min(b, len - 1);
            }
            else if (parts.Length > 1 && long.TryParse(parts[1], out var suffix)) start = Math.Max(0, len - suffix);
            if (start >= len) return env.CreateWebResourceResponse(null, 416, "Range Not Satisfiable", $"Content-Range: bytes */{len}");
            end = Math.Min(end, start + 16 * 1024 * 1024 - 1);
        }
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" or ".mkv" => "video/webm",
            _ => "video/mp4",
        };
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        fs.Position = start;
        var headers = $"Content-Type: {type}\nAccept-Ranges: bytes\nAccess-Control-Allow-Origin: *\nContent-Length: {end - start + 1}";
        if (partial) headers += $"\nContent-Range: bytes {start}-{end}/{len}";
        return env.CreateWebResourceResponse(new SliceStream(fs, end - start + 1), partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
    }

    VideoInfo Probe(string path)
    {
        var (code, stdout, _) = Run(ffprobe, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path);
        if (code != 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            JsonElement? v = null;
            int audio = 0;
            foreach (var s in root.GetProperty("streams").EnumerateArray())
            {
                var type = Str(s, "codec_type");
                bool pic = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && v == null && !pic) v = s;
                else if (type == "audio") audio++;
            }
            if (v is not JsonElement vs) return null;
            var fmt = root.GetProperty("format");
            double fps = Rate(Str(vs, "avg_frame_rate"));
            if (fps <= 0 || fps > 240) fps = Rate(Str(vs, "r_frame_rate"));
            if (fps <= 0 || fps > 240) fps = 30;
            return new VideoInfo(vs.GetProperty("width").GetInt32(), vs.GetProperty("height").GetInt32(), fps, Num(fmt, "duration"),
                (long)Num(fmt, "size"), (long)Num(fmt, "bit_rate"), (long)Num(vs, "bit_rate"), audio, Str(vs, "codec_name"), Num(fmt, "start_time"));
        }
        catch { return null; }
    }

    static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    static double Num(JsonElement e, string k) => double.TryParse(Str(e, k), NumberStyles.Float, Inv, out var d) ? d : 0;

    static double Rate(string r)
    {
        var p = (r ?? "").Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var a) && double.TryParse(p[1], NumberStyles.Float, Inv, out var b) && b > 0) return a / b;
        return 0;
    }

    static string F(double d) => d.ToString("0.###", Inv);

    static string UniqueOut(string dir, string src)
    {
        var name = Path.GetFileNameWithoutExtension(src);
        var p = Path.Combine(dir, name + "_clip.mp4");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name}_clip{i}.mp4");
        return p;
    }

    static readonly string LastDirFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipper", "lastdir.txt");

    /// <summary>Save As dialog, starting in the last folder exported to (or the source's folder). Null if cancelled.</summary>
    string AskOutPath(string src)
    {
        string dir = null;
        try { dir = File.ReadAllText(LastDirFile).Trim(); } catch { }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.GetDirectoryName(src);
        using var d = new SaveFileDialog
        {
            Title = "Export clip", Filter = "MP4 video|*.mp4", DefaultExt = "mp4", AddExtension = true, OverwritePrompt = true,
            InitialDirectory = dir, FileName = Path.GetFileName(UniqueOut(dir, src)),
        };
        while (d.ShowDialog(this) == DialogResult.OK)
        {
            if (string.Equals(Path.GetFullPath(d.FileName), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Can't overwrite the video you're cutting. Pick another name.", "Clipper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }
            try { File.WriteAllText(LastDirFile, Path.GetDirectoryName(d.FileName)); } catch { }
            return d.FileName;
        }
        return null;
    }

    async Task Export(ExportRequest q)
    {
        if (job != null || curPath == null || ffmpeg == null) { Send(new { type = "aborted" }); return; }
        await Task.Yield();   // get out of the WebMessageReceived handler before showing a modal dialog
        var src = curPath;
        var info = curInfo;
        var outPath = AskOutPath(src);
        if (outPath == null) { Send(new { type = "aborted" }); return; }
        double dur = q.End - q.Start;

        var attempts = new List<List<string>>();
        if (q.Mode == "copy")
            attempts.Add(CopyArgs(src, outPath, q));
        else
        {
            var enc = await hwEncoder;
            if (enc == "h264_nvenc") attempts.Add(EncodeArgs(src, outPath, q, info, gpuDecode: true, enc));
            if (enc != null) attempts.Add(EncodeArgs(src, outPath, q, info, gpuDecode: false, enc));
            attempts.Add(EncodeArgs(src, outPath, q, info, gpuDecode: false, null));
        }

        cancelled = false;
        var sw = Stopwatch.StartNew();
        string err = "";
        foreach (var args in attempts)
        {
            int code;
            (code, err) = await RunJob(args, dur);
            if (cancelled)
            {
                TryDelete(outPath);
                Send(new { type = "cancelled" });
                return;
            }
            if (code == 0 && File.Exists(outPath))
            {
                lastOut = outPath;
                Send(new { type = "done", name = Path.GetFileName(outPath), path = outPath, size = new FileInfo(outPath).Length, secs = sw.Elapsed.TotalSeconds });
                return;
            }
            TryDelete(outPath);
        }
        var tail = string.Join("\n", err.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
        Send(new { type = "error", msg = "Export failed: " + tail });
    }

    static List<string> CopyArgs(string src, string outPath, ExportRequest q) =>
    [
        "-hide_banner", "-y", "-nostats", "-progress", "pipe:1",
        "-ss", F(q.Start), "-t", F(q.End - q.Start), "-i", src,
        "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", outPath,
    ];

    /// <param name="enc">Hardware encoder (h264_nvenc / h264_amf / h264_qsv), or null for libx264 on the CPU.</param>
    static List<string> EncodeArgs(string src, string outPath, ExportRequest q, VideoInfo info, bool gpuDecode, string enc)
    {
        var a = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
        if (gpuDecode) a.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
        a.AddRange(["-ss", F(q.Start), "-t", F(q.End - q.Start), "-i", src]);

        var pix = enc == null ? "yuv420p" : "nv12";
        string vf = gpuDecode
            ? (q.Height > 0 ? $"scale_cuda=-2:{q.Height}:format=nv12" : "scale_cuda=format=nv12")
            : (q.Height > 0 ? $"scale=-2:{q.Height}:flags=lanczos,format={pix}" : $"format={pix}");
        var fc = $"[0:v:0]{vf}[v]";
        // ShadowPlay can record game + mic as separate tracks: mix them into one
        if (info.Audio > 1)
            fc += ";" + string.Concat(Enumerable.Range(0, info.Audio).Select(i => $"[0:a:{i}]")) + $"amix=inputs={info.Audio}:normalize=0[a]";
        a.AddRange(["-filter_complex", fc, "-map", "[v]"]);
        if (info.Audio > 1) a.AddRange(["-map", "[a]"]);
        else if (info.Audio == 1) a.AddRange(["-map", "0:a:0"]);

        string vk = q.VideoKbps + "k", max = q.VideoKbps * 3 / 2 + "k", buf = q.VideoKbps * 2 + "k";
        a.AddRange(enc switch
        {
            "h264_nvenc" => ["-c:v", enc, "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-multipass", "qres", "-spatial-aq", "1", "-profile:v", "high"],
            "h264_amf" => ["-c:v", enc, "-quality", "quality", "-rc", "vbr_peak"],
            "h264_qsv" => ["-c:v", enc, "-preset", "medium"],
            _ => (string[])["-c:v", "libx264", "-preset", "veryfast"],
        });
        a.AddRange(["-b:v", vk, "-maxrate", max, "-bufsize", buf]);
        a.AddRange(["-g", ((int)Math.Round(info.Fps * 2)).ToString(Inv)]);
        if (info.Audio > 0) a.AddRange(["-c:a", "aac", "-b:a", q.AudioKbps + "k"]);
        a.AddRange(["-movflags", "+faststart", outPath]);
        return a;
    }

    Task<(int Code, string Err)> RunJob(List<string> args, double dur)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var err = new StringBuilder();
        var tcs = new TaskCompletionSource<(int, string)>();
        double pct = 0;
        string speed = "";

        p.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (line == null) return;
            if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us))
                pct = Math.Clamp(us / 1e6 / dur, 0, 1);
            else if (line.StartsWith("speed="))
                speed = line[6..].Trim() == "N/A" ? "" : line[6..].Trim();
            else if (line.StartsWith("progress="))
            {
                double p2 = pct; string s2 = speed;
                BeginInvoke(() => Send(new { type = "progress", p = p2, speed = s2 }));
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (err) { err.AppendLine(e.Data); if (err.Length > 16000) err.Remove(0, err.Length - 8000); }
        };
        p.Exited += (_, _) =>
        {
            p.WaitForExit();
            string text;
            lock (err) text = err.ToString();
            tcs.TrySetResult((p.ExitCode, text));
        };

        job = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return tcs.Task.ContinueWith(t => { job = null; p.Dispose(); return t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void KillJob()
    {
        try { job?.Kill(true); } catch { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    static (int Code, string Out, string Err) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, errTask.Result);
    }
}
