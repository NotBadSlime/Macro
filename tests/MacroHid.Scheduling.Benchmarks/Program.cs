using System.Diagnostics;
using MacroHid.Core;
using MacroHid.Runtime;
var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount,1,PixelEvaluationMode.Skip,false,PrecisionMode.Balanced);
var simple = McrxParser.Parse("{\"steps\":[{\"type\":\"key.down\",\"key\":\"A\"},{\"type\":\"wait\",\"ms\":3},{\"type\":\"key.up\",\"key\":\"A\"}]}");
var parallel = new MacroDocument(1,"parallel",[new ParallelStep(new ParallelBranchStep("left",simple.Steps),new ParallelBranchStep("right",simple.Steps))]);
var longWait = McrxParser.Parse("{\"steps\":[{\"type\":\"key.down\",\"key\":\"A\"},{\"type\":\"wait\",\"ms\":1000},{\"type\":\"key.up\",\"key\":\"A\"}]}");
var startup = new List<double>(); var skew = new List<double>(); var stops = new List<double>(); var jitter = new List<double>();
var process=Process.GetCurrentProcess(); int handlesBefore=process.HandleCount;
int rounds = args.Length > 0 ? int.Parse(args[0]) : 300;
for(int i=0;i<rounds;i++) {
 var sink = new Probe();
 using(var executor=new MacroPlaybackExecutor(sink)) {
  long start=Stopwatch.GetTimestamp();
  await executor.RunAsync(simple,options,CancellationToken.None);
  var times=sink.Times.ToArray();
  if(i>=10) {startup.Add(Ms(times[0]-start));jitter.Add(Math.Abs(Ms(times[1]-times[0])-3));}
 }
 sink=new Probe();
 using(var executor=new MacroPlaybackExecutor(sink)) {
  await executor.RunAsync(parallel,options,CancellationToken.None);
  var times=sink.Times.ToArray();
  if(i>=10) skew.Add(Ms(times[1]-times[0]));
 }
 sink=new Probe();
 using(var executor=new MacroPlaybackExecutor(sink)) {
  using var cts=new CancellationTokenSource();
  var task=executor.RunAsync(longWait,options,cts.Token);
  if(!sink.Started.Wait(2000)) throw new Exception("start timeout");
  long start=Stopwatch.GetTimestamp(); cts.Cancel();
  try {await task;}catch(OperationCanceledException){}
  if(i>=10) stops.Add(Ms(Stopwatch.GetTimestamp()-start));
 }
 if ((i + 1) % (rounds > 300 ? 500 : 100) == 0) {
  GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
  Console.WriteLine($"checkpoint runs={i+1}: handles={process.HandleCount}; threads={process.Threads.Count}; privateMiB={process.PrivateMemorySize64/1048576.0:F2}");
 }
}
process.Refresh();
foreach(var pair in new[]{("startup",startup),("parallelSkew",skew),("stop",stops),("3msIntervalAbsoluteError",jitter)}) {
 var values=pair.Item2.Order().ToArray();
 Console.WriteLine($"{pair.Item1}: n={values.Length} p50={Rank(values,.5):F4}ms p95={Rank(values,.95):F4}ms p99={Rank(values,.99):F4}ms p99.9={Rank(values,.999):F4}ms max={values[^1]:F4}ms >0.1ms={Over(values,.1):P2} >0.25ms={Over(values,.25):P2} >1ms={Over(values,1):P2}");
}
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
Console.WriteLine($"handles before={handlesBefore} after={process.HandleCount}; threads={process.Threads.Count}; privateMiB={process.PrivateMemorySize64/1048576.0:F2}");
static double Ms(long ticks)=>ticks*1000.0/Stopwatch.Frequency;
// nearest-rank on an ascending array; p99.9 equals max until n >= 1000.
static double Rank(double[] sorted,double p)=>sorted[Math.Max(1,(int)Math.Ceiling(sorted.Length*p))-1];
static double Over(double[] sorted,double thresholdMs)=>sorted.Count(v=>v>thresholdMs)/(double)sorted.Length;
sealed class Probe : IMacroInputSink {
 public readonly System.Collections.Concurrent.ConcurrentQueue<long> Times=new();
 public readonly ManualResetEventSlim Started=new();
 public bool IsAvailable=>true;
 public void Submit(uint sequence,InputAction action) {Times.Enqueue(Stopwatch.GetTimestamp());Started.Set();}
 public InputSubmissionStats? GetStats()=>null;
}
