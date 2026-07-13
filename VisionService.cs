using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BoatDashboard;

/// <summary>
/// YOLO "visual sensors": periodically grabs a frame from each configured camera, runs a YOLOv8
/// object-detection model (ONNX) on it, and reports detections (person, boat, car, …) as events.
/// The host forwards those to MQTT (as vision sensors) and to the assistant (proactive alerts).
///
/// Fully self-contained and GRACEFUL: if the model file (<c>yolov8n.onnx</c>, placed next to the exe)
/// is missing, or a camera is unreachable, vision simply stays idle — nothing else is affected.
/// Model: export from Ultralytics (`yolo export model=yolov8n.pt format=onnx imgsz=640`) or download a
/// prebuilt yolov8n.onnx into the app directory. Input 640×640 NCHW RGB 0-1; output [1,84,8400].
/// </summary>
public sealed class VisionService : IDisposable
{
    public sealed record Detection(string Label, float Confidence, float X, float Y, float W, float H);

    private const int Size = 640;
    private const float ConfThreshold = 0.35f;
    private const float NmsThreshold = 0.45f;

    private static readonly string[] Coco =
    {
        "person","bicycle","car","motorcycle","airplane","bus","train","truck","boat","traffic light",
        "fire hydrant","stop sign","parking meter","bench","bird","cat","dog","horse","sheep","cow",
        "elephant","bear","zebra","giraffe","backpack","umbrella","handbag","tie","suitcase","frisbee",
        "skis","snowboard","sports ball","kite","baseball bat","baseball glove","skateboard","surfboard",
        "tennis racket","bottle","wine glass","cup","fork","knife","spoon","bowl","banana","apple",
        "sandwich","orange","broccoli","carrot","hot dog","pizza","donut","cake","chair","couch",
        "potted plant","bed","dining table","toilet","tv","laptop","mouse","remote","keyboard","cell phone",
        "microwave","oven","toaster","sink","refrigerator","book","clock","vase","scissors","teddy bear",
        "hair drier","toothbrush",
    };

    private readonly Func<IReadOnlyList<CameraDef>> _cameras;
    private readonly CancellationTokenSource _cts = new();
    private InferenceSession? _session;
    private string? _inputName;

    /// <summary>Raised with (cameraIndex, cameraName, detections) after each frame is analysed.</summary>
    public event Action<int, string, IReadOnlyList<Detection>>? OnDetections;

    public bool Enabled => _session is not null;
    public string Status { get; private set; } = "idle";

    public VisionService(Func<IReadOnlyList<CameraDef>> cameras) => _cameras = cameras;

    public void Start()
    {
        var modelPath = Path.Combine(AppContext.BaseDirectory, "yolov8n.onnx");
        if (!File.Exists(modelPath))
        {
            Status = "no model (drop yolov8n.onnx next to the exe to enable)";
            Ip2slClient.Log("[vision] " + Status);
            return;
        }
        try
        {
            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();
            Status = "running";
            Ip2slClient.Log("[vision] model loaded, analysing camera frames");
            _ = Task.Run(LoopAsync);
        }
        catch (Exception ex)
        {
            Status = "model load failed: " + ex.Message;
            Ip2slClient.Log("[vision] " + Status);
        }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var cams = _cameras();
            for (int i = 0; i < cams.Count && !_cts.IsCancellationRequested; i++)
            {
                try
                {
                    var jpeg = await LocalServer.FetchSnapshotAsync(cams[i].Url);
                    if (jpeg is null) continue;
                    var dets = Analyze(jpeg);
                    var name = string.IsNullOrWhiteSpace(cams[i].Name) ? $"cam{i}" : cams[i].Name;
                    OnDetections?.Invoke(i, name, dets);   // always fire so the UI can clear stale boxes
                }
                catch (Exception ex) { Ip2slClient.Log("[vision] frame error: " + ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); } catch { break; }
        }
    }

    /// <summary>Runs the model on one JPEG frame and returns the surviving detections.</summary>
    public IReadOnlyList<Detection> Analyze(byte[] jpeg)
    {
        if (_session is null || _inputName is null) return Array.Empty<Detection>();
        // A valid camera JPEG is comfortably over a few KB; anything tiny is a truncated/failed fetch.
        // Feeding malformed data to the NATIVE ONNX runtime can raise an uncatchable access violation
        // that hard-kills the process, so we reject bad frames here before inference.
        if (jpeg is null || jpeg.Length < 1024) return Array.Empty<Detection>();

        var input = Preprocess(jpeg);
        if (input is null) return Array.Empty<Detection>();   // frame failed to decode to a sane bitmap
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var output = results.First().AsTensor<float>();   // [1, 84, 8400]

        int channels = output.Dimensions[1];              // 84 = 4 box + 80 classes
        int anchors = output.Dimensions[2];               // 8400
        int nClasses = channels - 4;
        var dets = new List<Detection>();

        for (int a = 0; a < anchors; a++)
        {
            float best = 0f; int bestC = -1;
            for (int c = 0; c < nClasses; c++)
            {
                float s = output[0, 4 + c, a];
                if (s > best) { best = s; bestC = c; }
            }
            if (best < ConfThreshold || bestC < 0) continue;
            float cx = output[0, 0, a], cy = output[0, 1, a], w = output[0, 2, a], h = output[0, 3, a];
            dets.Add(new Detection(bestC < Coco.Length ? Coco[bestC] : bestC.ToString(), best,
                (cx - w / 2) / Size, (cy - h / 2) / Size, w / Size, h / Size));
        }
        return Nms(dets);
    }

    // Decode + resize to 640×640 (WPF imaging, no external image lib) → NCHW RGB float tensor 0-1.
    // Returns null if the frame won't decode to a valid, non-zero-dimension bitmap — a zero dimension
    // would make the scale factor Infinity/NaN and hand garbage to the native model (which can crash it).
    private static DenseTensor<float>? Preprocess(byte[] jpeg)
    {
        BitmapSource frame;
        try
        {
            frame = BitmapFrame.Create(new MemoryStream(jpeg),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch { return null; }
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return null;
        var scaled = new TransformedBitmap(frame,
            new ScaleTransform((double)Size / frame.PixelWidth, (double)Size / frame.PixelHeight));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

        int stride = Size * 4;
        var px = new byte[Size * stride];
        bgra.CopyPixels(px, stride, 0);

        var t = new DenseTensor<float>(new[] { 1, 3, Size, Size });
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int o = y * stride + x * 4;   // BGRA
                t[0, 0, y, x] = px[o + 2] / 255f;  // R
                t[0, 1, y, x] = px[o + 1] / 255f;  // G
                t[0, 2, y, x] = px[o + 0] / 255f;  // B
            }
        return t;
    }

    // Non-max suppression: keep the highest-confidence box, drop overlapping duplicates.
    private static List<Detection> Nms(List<Detection> dets)
    {
        var kept = new List<Detection>();
        foreach (var d in dets.OrderByDescending(d => d.Confidence))
        {
            bool overlap = kept.Any(k => k.Label == d.Label && Iou(k, d) > NmsThreshold);
            if (!overlap) kept.Add(d);
        }
        return kept;
    }

    private static float Iou(Detection a, Detection b)
    {
        float x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        float inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        float uni = a.W * a.H + b.W * b.H - inter;
        return uni <= 0 ? 0 : inter / uni;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _session?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
