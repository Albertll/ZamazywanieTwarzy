#:package Microsoft.ML.OnnxRuntime@1.31.1
// Wejścia i wyjścia modeli ONNX: dotnet run proba\meta.cs -- model1.onnx [model2.onnx ...]
using Microsoft.ML.OnnxRuntime;
foreach (var p in args) {
  using var o = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
  using var s = new InferenceSession(p, o);
  Console.WriteLine(Path.GetFileName(p));
  foreach (var kv in s.InputMetadata) Console.WriteLine($"  IN  {kv.Key} [{string.Join(",", kv.Value.Dimensions)}]");
  foreach (var n in s.OutputNames) Console.WriteLine($"  OUT {n} [{string.Join(",", s.OutputMetadata[n].Dimensions)}]");
}
