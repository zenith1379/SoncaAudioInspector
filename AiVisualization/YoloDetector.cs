using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace AiVisualization
{
    public class YoloDetection
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public Rect Box { get; set; } // bounding box
    }

    public class DetectionResult
    {
        public float? Score { get; set; }
        public bool? IsAnomaly { get; set; }
        public float ElapsedMs { get; set; }
        public List<YoloDetection> Detections { get; set; } = new List<YoloDetection>();
        public string ModelType { get; set; } = "supervised";
    }

    public class YoloDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly Dictionary<int, string> _classNames = new Dictionary<int, string>();
        private readonly string _inputName;
        private readonly int _modelWidth;
        private readonly int _modelHeight;

        public YoloDetector(string modelPath)
        {
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Model file not found: {modelPath}");
            }

            // Initialize ONNX Session
            var options = new SessionOptions();
            _session = new InferenceSession(modelPath, options);

            // Get input name
            _inputName = _session.InputMetadata.Keys.First();

            // Resolve input dimensions dynamically
            var inputMeta = _session.InputMetadata[_inputName];
            if (inputMeta.Dimensions.Length >= 4)
            {
                _modelHeight = inputMeta.Dimensions[2] > 0 ? inputMeta.Dimensions[2] : 640;
                _modelWidth = inputMeta.Dimensions[3] > 0 ? inputMeta.Dimensions[3] : 640;
            }
            else
            {
                _modelHeight = 640;
                _modelWidth = 640;
            }

            // Try to load class names from model metadata
            LoadClassNamesFromMetadata();
        }

        private void LoadClassNamesFromMetadata()
        {
            try
            {
                var metadata = _session.ModelMetadata.CustomMetadataMap;
                if (metadata.TryGetValue("names", out string? namesStr) && !string.IsNullOrEmpty(namesStr))
                {
                    // parse using Regex to handle both {0: 'nguoc'} and {"0": "nguoc"} formats
                    var matches = System.Text.RegularExpressions.Regex.Matches(namesStr, @"(\d+)\s*:\s*['""]([^'""]+)['""]");
                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        if (int.TryParse(match.Groups[1].Value, out int id))
                        {
                            _classNames[id] = match.Groups[2].Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to load class names from model metadata: {ex.Message}");
            }
        }

        public string GetClassName(int classId)
        {
            if (_classNames.TryGetValue(classId, out string? name))
            {
                return name;
            }
            return classId.ToString();
        }

        public DetectionResult Predict(Mat frame, float confThreshold = 0.25f, string passClassesStr = "ok,pass,good")
        {
            if (frame.Empty())
            {
                throw new ArgumentException("Input frame is empty.");
            }

            var stopwatch = Stopwatch.StartNew();

            int origWidth = frame.Width;
            int origHeight = frame.Height;

            // 1. Preprocess: Resize to model dimensions
            using var resized = new Mat();
            Cv2.Resize(frame, resized, new Size(_modelWidth, _modelHeight));

            // Convert BGR to RGB
            using var rgb = new Mat();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

            // Create CHW float array normalized to [0, 1]
            float[] inputData = new float[1 * 3 * _modelHeight * _modelWidth];
            for (int y = 0; y < _modelHeight; y++)
            {
                for (int x = 0; x < _modelWidth; x++)
                {
                    Vec3b pixel = rgb.At<Vec3b>(y, x);
                    inputData[0 * _modelHeight * _modelWidth + y * _modelWidth + x] = pixel.Item0 / 255.0f; // Red
                    inputData[1 * _modelHeight * _modelWidth + y * _modelWidth + x] = pixel.Item1 / 255.0f; // Green
                    inputData[2 * _modelHeight * _modelWidth + y * _modelWidth + x] = pixel.Item2 / 255.0f; // Blue
                }
            }

            // 2. Run Inference
            var inputTensor = new DenseTensor<float>(inputData, new int[] { 1, 3, _modelHeight, _modelWidth });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
            };

            using var results = _session.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            // Output shape is [1, 300, 6]
            // Each row: [x1, y1, x2, y2, confidence, class_id]
            var detections = new List<YoloDetection>();
            float scaleX = origWidth / (float)_modelWidth;
            float scaleY = origHeight / (float)_modelHeight;

            for (int i = 0; i < 300; i++)
            {
                float x1 = outputTensor[0, i, 0];
                float y1 = outputTensor[0, i, 1];
                float x2 = outputTensor[0, i, 2];
                float y2 = outputTensor[0, i, 3];
                float confidence = outputTensor[0, i, 4];
                float classIdFloat = outputTensor[0, i, 5];

                if (confidence >= confThreshold)
                {
                    int classId = (int)classIdFloat;
                    string className = GetClassName(classId);

                    // Scale back to original coordinates
                    int rx1 = (int)Math.Max(0, Math.Round(x1 * scaleX));
                    int ry1 = (int)Math.Max(0, Math.Round(y1 * scaleY));
                    int rx2 = (int)Math.Min(origWidth, Math.Round(x2 * scaleX));
                    int ry2 = (int)Math.Min(origHeight, Math.Round(y2 * scaleY));

                    detections.Add(new YoloDetection
                    {
                        ClassId = classId,
                        ClassName = className,
                        Confidence = confidence,
                        Box = new Rect(rx1, ry1, rx2 - rx1, ry2 - ry1)
                    });
                }
            }

            stopwatch.Stop();
            float elapsedMs = (float)stopwatch.Elapsed.TotalMilliseconds;

            // Determine anomaly state
            // Split pass classes
            var passClasses = passClassesStr.Split(',')
                .Select(s => s.Trim().ToLower())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();

            // An anomaly exists if we have detections whose class name is NOT in passClasses
            var failDetections = detections
                .Where(d => !passClasses.Contains(d.ClassName.ToLower()))
                .ToList();

            bool isAnomaly = failDetections.Count > 0;
            float? score = detections.Count > 0 ? detections.Max(d => d.Confidence) : null;

            return new DetectionResult
            {
                Score = score,
                IsAnomaly = isAnomaly,
                ElapsedMs = elapsedMs,
                Detections = detections
            };
        }

        public void DrawDetections(Mat image, List<YoloDetection> detections, string passClassesStr = "ok,pass,good")
        {
            var passClasses = passClassesStr.Split(',')
                .Select(s => s.Trim().ToLower())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet();

            foreach (var det in detections)
            {
                bool isPass = passClasses.Contains(det.ClassName.ToLower());
                Scalar color = isPass ? Scalar.FromRgb(24, 128, 56) : Scalar.FromRgb(217, 48, 37); // Green for OK, Red for Fail

                // Draw bounding box
                Cv2.Rectangle(image, det.Box, color, 2);

                // Draw label background
                string label = $"{det.ClassName} {det.Confidence:P0}";
                int baseLine = 0;
                Size labelSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.5, 1, out baseLine);
                int labelY = Math.Max(det.Box.Y, labelSize.Height + 5);

                Cv2.Rectangle(image, 
                    new Rect(det.Box.X, labelY - labelSize.Height - 5, labelSize.Width + 10, labelSize.Height + 10), 
                    color, 
                    -1); // Solid color fill

                // Draw label text
                Cv2.PutText(image, 
                    label, 
                    new Point(det.Box.X + 5, labelY - 2), 
                    HersheyFonts.HersheySimplex, 
                    0.5, 
                    Scalar.White, 
                    1, 
                    LineTypes.AntiAlias);
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
